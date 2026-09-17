# -*- coding: utf-8 -*-
"""自動將結構構架中心點（藍色圓點）與實體切齊端點（三角形控制點）自動對齊至主模型及連結模型(RVT Links)中的構架/柱/牆邊界。"""

__title__ = '結構構架\n自動對齊'
__doc__ = '自動將選取或當前視圖中的結構構架對齊主模型及連結模型 (Revit Links) 中的結構構架、柱、牆邊界。'
__author__ = 'MCP Antigravity'

import math
import re
import clr
clr.AddReference('System')
import System

from Autodesk.Revit import DB
from Autodesk.Revit.DB import Structure as ST
from pyrevit import revit, forms, script

output = script.get_output()

def get_all_target_faces_and_solids(geom_elem, transform=None):
    """擷取所有 3D Solid 及 Faces，支援連結模型 (RVT Link) 座標轉換"""
    faces = []
    solids = []
    if geom_elem is None:
        return faces, solids
    for obj in geom_elem:
        if isinstance(obj, DB.Solid):
            if obj.Volume > 1e-6 or (obj.Faces and not obj.Faces.IsEmpty):
                if transform and not transform.IsIdentity:
                    try:
                        t_solid = DB.SolidUtils.CreateTransformed(obj, transform)
                        solids.append(t_solid)
                        for face in t_solid.Faces:
                            faces.append(face)
                    except Exception:
                        for face in obj.Faces:
                            faces.append(face)
                else:
                    solids.append(obj)
                    for face in obj.Faces:
                        faces.append(face)
        elif isinstance(obj, DB.GeometryInstance):
            inst_geom = obj.GetInstanceGeometry()
            if inst_geom:
                sub_f, sub_s = get_all_target_faces_and_solids(inst_geom, transform)
                faces.extend(sub_f)
                solids.extend(sub_s)
    return faces, solids

def bbox_intersects(b1_min, b1_max, b2_min, b2_max):
    return (b1_min.X <= b2_max.X and b1_max.X >= b2_min.X and
            b1_min.Y <= b2_max.Y and b1_max.Y >= b2_min.Y and
            b1_min.Z <= b2_max.Z and b1_max.Z >= b2_min.Z)

# 相交計算統計（診斷用：若對齊數為 0，看這些數字判斷卡在哪一層）
_isect_stats = {'calls': 0, 'hits': 0, 'errors': 0, 'first_error': None}

def intersect_with_results(geo_obj, curve):
    """呼叫 Face/Curve.Intersect(curve, out IntersectionResultArray)。

    重要：IronPython 對單參數呼叫 .Intersect(curve) 會解析到
    Intersect(Curve) 多載，只回傳 SetComparisonResult（enum、不可迭代），
    永遠拿不到交點 → 之前對齊數 0 的根因。必須用 clr.Reference 明確
    取得 out 參數。CPython 引擎則以 tuple 回傳，兩者都相容。
    回傳 IntersectionResultArray 或 None。
    """
    _isect_stats['calls'] += 1
    arr = None
    res = None
    try:
        ref = clr.Reference[DB.IntersectionResultArray]()
        res = geo_obj.Intersect(curve, ref)
        arr = ref.Value
    except TypeError:
        # CPython/pythonnet：out 參數以 tuple 回傳
        try:
            r = geo_obj.Intersect(curve)
            if isinstance(r, tuple) and len(r) > 1:
                res, arr = r[0], r[1]
            else:
                return None
        except Exception as ex:
            _isect_stats['errors'] += 1
            if _isect_stats['first_error'] is None:
                _isect_stats['first_error'] = str(ex)
            return None
    except Exception as ex:
        _isect_stats['errors'] += 1
        if _isect_stats['first_error'] is None:
            _isect_stats['first_error'] = str(ex)
        return None
    if res != DB.SetComparisonResult.Overlap:
        return None
    if arr is None or arr.IsEmpty:
        return None
    _isect_stats['hits'] += 1
    return arr

def find_target_center_and_face(doc, current_framing, end_point, ray_dir, target_items, max_dist_feet, geom_opt_3d, our_half_w):
    """
    對齊主模型與連結模型 (RVT Links)
    回傳 (target_center_pt, target_face_pt, target_id, target_cat)
    """
    best_face = None
    best_a = None            # 近端面沿 outward 的帶號距離(≤0=retract)，取最大(retract 最小=樑先碰到那根)
    best_target_id = None
    best_target_cat = None

    # --- DEBUG 收集（診斷用）---
    dbg = {
        'scanned': 0,      # 掃過的目標數
        'contained': [],   # [(品類, id, is_link)] 端點落 bbox 內(相接)的候選
        'hits': [],        # [(近端面延伸mm, 品類, id, is_link)] 每個相接候選的近端面 butt 值
    }

    cm = 150.0 / 304.8            # 相接容差
    tol_out = max_dist_feet       # 近端面不可比搜尋半徑更深(保險上限)

    for item in target_items:
        target = item['element']
        transform = item['transform']

        if not item['is_link'] and target.Id.IntegerValue == current_framing.Id.IntegerValue:
            continue

        bbox = target.get_BoundingBox(None)
        if not bbox:
            continue

        # bbox 8 角 → 世界座標(連結檔套 transform)
        corners = []
        for cx in (bbox.Min.X, bbox.Max.X):
            for cy in (bbox.Min.Y, bbox.Max.Y):
                for cz in (bbox.Min.Z, bbox.Max.Z):
                    p = DB.XYZ(cx, cy, cz)
                    if transform and not transform.IsIdentity:
                        p = transform.OfPoint(p)
                    corners.append(p)
        zs = [p.Z for p in corners]
        t_zmin, t_zmax = min(zs), max(zs)

        dbg['scanned'] += 1

        # 橫向對齊：構材必須「跨在樑的軸線上」= 端點落在其「垂直本樑水平方向」與 Z 範圍內(±150mm)。
        # 沿軸不設限 → 允許轉角(樑短、留間隙)也能找到相接構材。橫向偏開的平行樑/旁邊不相接的柱則排除。
        # 橫向要在本樑座標系量(perp 投影)，不能拿世界 Y：三組校準樑都沿 X 所以當時看不出差別，
        # 換成沿 Y 的樑，比世界 Y 等於比「沿軸」，側邊 2m 外的柱會過關再被柱豁免放行。
        perp = DB.XYZ(-ray_dir.Y, ray_dir.X, 0.0)
        pprojs = [(p - end_point).DotProduct(perp) for p in corners]
        if not (min(pprojs) - cm <= 0.0 <= max(pprojs) + cm and
                t_zmin - cm <= end_point.Z <= t_zmax + cm):
            continue

        # 近端面 = bbox 8 角投影到 outward 取最小(body 側)。用 bbox「全寬」而非薄腹板面。
        projs = [(p - end_point).DotProduct(ray_dir) for p in corners]
        a = min(projs)   # 近端面帶號距離：<0=retract(穿越型 T接), >0=extend(轉角型 L接)
        b = max(projs)

        # 沿軸窗口：近端面須在 ±搜尋半徑內(可 retract 或 extend 到面；排除遠處不相接的柱)。
        if a < -tol_out or a > tol_out:
            continue

        # 樑(結構構架)須「垂直穿越/轉角」= 沿軸footprint小；排除「共線端對端續接」的樑(沿軸很長)。
        # 柱不受此限(樑常直接接大柱面，柱沿軸footprint本來就大)。
        cat_name = target.Category.Name if target.Category else "?"
        is_col = ("柱" in cat_name) or ("Column" in cat_name)
        maxwidth = 600.0 / 304.8
        if (not is_col) and (b - a) > maxwidth:
            continue

        # T接 vs L轉角判定：看構材在「垂直本樑」水平方向是否跨端點兩側(同上面的 perp 投影)。
        #   貫穿(兩側都有料)=T接 → retract 到近端面 a。
        #   只單側(構材也在此終止)=轉角 → extend 到對接樑遠面，量 ≈ 本樑半寬(截圖 75=150/2)。
        tperp = 150.0 / 304.8
        through = (min(pprojs) < -tperp and max(pprojs) > tperp)

        # 相接構材「沿軸中心 c」+「真實半寬 mem_half」定面 → 貼到真鋼面(flush)。
        # 關鍵：直接用 bbox 面會因 join 膨脹(真鋼150 卻 bbox 433)而停在鋼面外側 → 留縫。
        # 改由型號名解析真寬(RH300x150→150)繞開污染 bbox；柱等無法解析則退回 bbox 半寬。
        c = (a + b) * 0.5
        mem_half = None
        try:
            _m = re.search(r'[Hh](\d+)[xX](\d+)', target.Name)
            if _m:
                mem_half = (float(_m.group(2)) * 0.5) / 304.8
        except Exception:
            pass
        if not mem_half:
            mem_half = (b - a) * 0.5
        if is_col or through:
            face_a = c - mem_half      # T接/接柱：真實近端面(retract，貼齊無縫)
        else:
            face_a = c + mem_half      # L轉角：真實遠端面(extend)

        dbg['contained'].append((cat_name, target.Id.IntegerValue, item['is_link']))
        dbg['hits'].append((face_a * 304.8, cat_name + ("/T" if (is_col or through) else "/L轉角"),
                            target.Id.IntegerValue, item['is_link']))

        # 選「最靠端點(|face_a| 最小)」者 = 樑最先碰到的相接面。
        if best_a is None or abs(face_a) < abs(best_a):
            best_a = face_a
            best_face = end_point + ray_dir * face_a
            best_target_id = target.Id
            best_target_cat = cat_name

    return None, best_face, best_target_id, best_target_cat, dbg

def print_dbg(label, end_point, dbg):
    """診斷輸出：看清楚「相接判定」放行了誰、射線命中了哪些面、距離多少。
    若對接樑沒出現在『相接候選』→ containment 容差(150mm)或 bbox 判定要調；
    若出現在候選卻沒有面命中 → 射線沒打到它的面(Z/Y 對不齊或連結檔幾何問題)。"""
    print("  [DEBUG] {} 端點=({:.0f}, {:.0f}, {:.0f})mm | 掃描 {} 個 | 相接候選 {} 個 | 面命中 {} 個".format(
        label, end_point.X * 304.8, end_point.Y * 304.8, end_point.Z * 304.8,
        dbg['scanned'], len(dbg['contained']), len(dbg['hits'])))
    if not dbg['contained']:
        print("      (無相接候選：端點沒落在任何 柱/樑 的範圍盒內 ±150mm)")
    for c in dbg['contained'][:8]:
        print("      相接候選: {} ID={} {}".format(c[0], c[1], "連結檔" if c[2] else "主模型"))
    if dbg['contained'] and not dbg['hits']:
        print("      (有相接候選但射線沒命中任何面 → 面幾何/Z 對不齊)")
    for h in sorted(dbg['hits'], key=lambda x: abs(x[0]))[:8]:
        print("      面命中: {:+.1f}mm {} ID={} {}".format(
            h[0], h[1], h[2], "連結檔" if h[3] else "主模型"))


# --- 主程式 ---
try:
    doc = revit.doc
    active_view = doc.ActiveView
    selection = revit.get_selection()

    framing_elements = []
    if selection:
        for elem in selection:
            if elem.Category and elem.Category.Id.IntegerValue == int(DB.BuiltInCategory.OST_StructuralFraming):
                framing_elements.append(elem)

    if not framing_elements:
        res = forms.alert(
            "目前未選取任何結構構架。\n是否自動對齊「當前視圖內的所有結構構架」？",
            title="結構構架對齊",
            yes=True, no=True
        )
        if res:
            collector = DB.FilteredElementCollector(doc, active_view.Id)\
                .OfCategory(DB.BuiltInCategory.OST_StructuralFraming)\
                .WhereElementIsNotElementType()\
                .OfClass(DB.FamilyInstance)
            framing_elements = list(collector)
        else:
            forms.alert("已取消操作。", title="結構構架對齊")
            script.exit()

    if not framing_elements:
        forms.alert("未在範圍內找到任何結構構架。", title="結構構架對齊")
        script.exit()

    # 只對柱 + 結構構架對齊（樓板/牆/軸線移除：樓板 bbox 中心離樑端十幾米，是之前樑跑掉的元凶）
    target_cats = [
        DB.BuiltInCategory.OST_StructuralFraming,
        DB.BuiltInCategory.OST_StructuralColumns,
        DB.BuiltInCategory.OST_Columns
    ]

    target_items = []

    # 1. 收集主模型目標元素
    for cat in target_cats:
        elems = DB.FilteredElementCollector(doc, active_view.Id)\
            .OfCategory(cat)\
            .WhereElementIsNotElementType()\
            .ToElements()
        for e in elems:
            target_items.append({'element': e, 'transform': None, 'is_link': False})

    print("主模型目標元素收集完成，共 {} 個目標物件。".format(len(target_items)))

    # 2. 收集連結模型 (RVT Links) 中的目標元素
    link_instances = DB.FilteredElementCollector(doc)\
        .OfCategory(DB.BuiltInCategory.OST_RvtLinks)\
        .WhereElementIsNotElementType()\
        .ToElements()

    print("找到 {} 個連結模型實體 (RVT Links)。".format(len(link_instances)))

    for link_inst in link_instances:
        if not isinstance(link_inst, DB.RevitLinkInstance):
            continue
        try:
            link_doc = link_inst.GetLinkDocument()
            if not link_doc:
                continue
            tf = link_inst.GetTotalTransform()
            added_link_count = 0
            for cat in target_cats:
                link_elems = DB.FilteredElementCollector(link_doc)\
                    .OfCategory(cat)\
                    .WhereElementIsNotElementType()\
                    .ToElements()
                for le in link_elems:
                    target_items.append({'element': le, 'transform': tf, 'is_link': True})
                    added_link_count += 1
            print("  [連結檔] {} 成功加入 {} 個對齊目標元素。".format(link_doc.Title, added_link_count))
        except Exception as link_ex:
            print("! 讀取連結檔 {} 發生錯誤: {}".format(link_inst.Name, str(link_ex)))

    print("--- 開始進行結構構架對齊計算 (共 {} 個搜尋目標) ---".format(len(target_items)))

    MAX_FACE_MM = 700.0                    # 沿軸窗口：近端面 retract/extend 上限。夠含相接大梁半寬(~216)+轉角間隙+柱600半寬(300)；擋掉 900mm 外不相接的柱
    max_dist_feet = MAX_FACE_MM / 304.8
    geom_opt_3d = DB.Options()
    geom_opt_3d.ComputeReferences = True
    geom_opt_3d.DetailLevel = DB.ViewDetailLevel.Fine

    triangle_aligned_count = 0
    manual_count = 0
    MAX_CANDIDATES = 4          # 相接候選 >此數 = 繁忙節點、選擇模糊 → 跳過不動、標記手動

    with revit.Transaction("pyRevit: 三角形切齊結構構架端點至鄰近柱/樑面"):
        # ★ 第1段：先把所有選取樑的延伸歸零 → 消除前次殘留污染(idempotent，再跑同結果、不累積)、
        # 確保下面讀到的是乾淨 bbox。以前反覆失敗的隱藏元兇就是沒歸零、延伸疊加污染幾何。
        for framing in framing_elements:
            for _bip in (DB.BuiltInParameter.START_EXTENSION, DB.BuiltInParameter.END_EXTENSION):
                _pr = framing.get_Parameter(_bip)
                if _pr and not _pr.IsReadOnly:
                    try:
                        _pr.Set(0.0)
                    except Exception:
                        pass
        doc.Regenerate()

        # 第2段：逐根計算並套用
        for framing in framing_elements:
            loc = framing.Location
            if not isinstance(loc, DB.LocationCurve):
                continue
            beam_line = loc.Curve
            if not isinstance(beam_line, DB.Line):
                continue

            p0 = beam_line.GetEndPoint(0)
            p1 = beam_line.GetEndPoint(1)
            v = (p1 - p0).Normalize()

            # ★ 關鍵：樑中心線(LocationCurve)常在鋼樑實體「上方」(Z向對正=頂部 + Z偏移)，
            # 例：本樑中心線 Z=17140，但實體 bbox 頂也剛好 17140 → 水平射線從中心線Z射出
            # 會「掠過」所有鋼樑實體頂面 → 只打得到貫穿全高的柱、打不到相接的樑(真因)。
            # 解法：射線改從「實體 bbox 中點Z」射出，確保穿過相接構材的實體中央。
            # v 是水平的 → 改 Z 不影響延伸值(延伸沿 ±v 只用 X/Y)。
            our_half_w = 75.0 / 304.8   # 預設(半個 150 寬樑)
            bb_self = framing.get_BoundingBox(None)
            if bb_self:
                ray_z = (bb_self.Min.Z + bb_self.Max.Z) * 0.5
                p0 = DB.XYZ(p0.X, p0.Y, ray_z)
                p1 = DB.XYZ(p1.X, p1.Y, ray_z)
            # 本樑半寬(轉角 extend 量)：優先從型號名解析真實斷面寬(RH300x150→150→半75)，
            # 避免 join 污染的 bbox(繁忙接點會膨脹成假 433)。解析失敗才退回 bbox 垂直半寬。
            true_half = None
            try:
                m = re.search(r'[Hh](\d+)[xX](\d+)', framing.Name)
                if m:
                    true_half = (float(m.group(2)) * 0.5) / 304.8
            except Exception:
                pass
            if true_half:
                our_half_w = true_half
            elif bb_self:
                if abs(v.X) >= abs(v.Y):
                    our_half_w = (bb_self.Max.Y - bb_self.Min.Y) * 0.5
                else:
                    our_half_w = (bb_self.Max.X - bb_self.Min.X) * 0.5

            # 位置不改：藍點(軸線)完全不動，只把三角形(實體端)延伸/切齊到鄰近柱或樑的面。
            # 起點外向 = -v；終點外向 = +v。延伸值 = 端點沿「外向」到最近面的帶號距離。

            # --- 起點 (End 0)，外向 = -v ---
            _c0, face0, _t0, tcat0, dbg0 = find_target_center_and_face(doc, framing, p0, -v, target_items, max_dist_feet, geom_opt_3d, our_half_w)
            print_dbg("起點(End0)", p0, dbg0)
            # 繁忙節點保險：相接候選過多 → 選擇模糊 → 跳過不動、標記手動。
            if face0 and len(dbg0['contained']) > MAX_CANDIDATES:
                manual_count += 1
                print("構架 ID {} 起點：相接候選 {} 個(>{}) = 繁忙節點，跳過不動、需手動確認".format(
                    framing.Id.IntegerValue, len(dbg0['contained']), MAX_CANDIDATES))
                face0 = None
            if face0:
                try:
                    # 起點外向為 -v（先前用 +v 導致起點延伸「反向」= 右端貼不到面的真因）
                    ext0 = (face0 - p0).DotProduct(-v)
                    try:
                        ST.StructuralFramingUtils.DisallowJoinAtEnd(framing, 0)
                    except Exception:
                        pass
                    p_ext0 = framing.get_Parameter(DB.BuiltInParameter.START_EXTENSION) or framing.LookupParameter("起點延伸")
                    if p_ext0 and not p_ext0.IsReadOnly:
                        p_ext0.Set(ext0)
                        triangle_aligned_count += 1
                        print("構架 ID {} 起點三角形切齊 品類={} (延伸 {:.1f} mm)".format(
                            framing.Id.IntegerValue, tcat0, ext0 * 304.8))
                except Exception as ex:
                    print("! 起點三角形切齊失敗: {}".format(str(ex)))

            # --- 終點 (End 1)，外向 = +v ---
            _c1, face1, _t1, tcat1, dbg1 = find_target_center_and_face(doc, framing, p1, v, target_items, max_dist_feet, geom_opt_3d, our_half_w)
            print_dbg("終點(End1)", p1, dbg1)
            if face1 and len(dbg1['contained']) > MAX_CANDIDATES:
                manual_count += 1
                print("構架 ID {} 終點：相接候選 {} 個(>{}) = 繁忙節點，跳過不動、需手動確認".format(
                    framing.Id.IntegerValue, len(dbg1['contained']), MAX_CANDIDATES))
                face1 = None
            if face1:
                try:
                    ext1 = (face1 - p1).DotProduct(v)
                    try:
                        ST.StructuralFramingUtils.DisallowJoinAtEnd(framing, 1)
                    except Exception:
                        pass
                    p_ext1 = framing.get_Parameter(DB.BuiltInParameter.END_EXTENSION) or framing.LookupParameter("終點延伸")
                    if p_ext1 and not p_ext1.IsReadOnly:
                        p_ext1.Set(ext1)
                        triangle_aligned_count += 1
                        print("構架 ID {} 終點三角形切齊 品類={} (延伸 {:.1f} mm)".format(
                            framing.Id.IntegerValue, tcat1, ext1 * 304.8))
                except Exception as ex:
                    print("! 終點三角形切齊失敗: {}".format(str(ex)))

    summary_msg = "對齊處理完成！\n處理構架數：{}\n位置(藍點)不變；三角形實體切齊端點數：{}\n繁忙節點跳過(需手動)：{} 端\n(對接主模型與連結檔 RVT Links 的柱/樑面)".format(
        len(framing_elements), triangle_aligned_count, manual_count
    )
    print(summary_msg)
    print("診斷: Intersect 呼叫 {} 次 / 命中 {} 次 / 例外 {} 次".format(
        _isect_stats['calls'], _isect_stats['hits'], _isect_stats['errors']))
    if _isect_stats['first_error']:
        print("診斷: 首個 Intersect 例外 = {}".format(_isect_stats['first_error']))
    forms.alert(summary_msg, title="對齊完成")

except Exception as ex:
    forms.alert("對齊過程發生錯誤:\n{}".format(str(ex)), title="錯誤")
