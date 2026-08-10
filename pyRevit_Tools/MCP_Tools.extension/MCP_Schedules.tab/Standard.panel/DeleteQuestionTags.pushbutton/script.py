# -*- coding: utf-8 -*-
"""自動掃描當前視圖並刪除所有顯示 ?? 或包含 ? 的無效鋼樑標籤與結構柱標籤。"""

__title__ = '刪除\n無效標籤'
__author__ = 'MCP'

from Autodesk.Revit import DB
from pyrevit import revit, forms

doc = revit.doc
active_view = doc.ActiveView

# 讓使用者選擇要清理的目標類別
selected_cat = forms.CommandSwitchWindow.show(
    ["結構構架標籤 (樑標籤)", "結構柱標籤 (柱標籤)", "全部標籤 (樑 + 柱)"],
    message="請選擇要掃描與清理的無效標籤類型："
)

if not selected_cat:
    forms.alert("已取消操作。", title="取消")
else:
    cats_to_scan = []
    if "樑" in selected_cat or "全部" in selected_cat:
        cats_to_scan.append(("結構構架標籤", DB.BuiltInCategory.OST_StructuralFramingTags))
    if "柱" in selected_cat or "全部" in selected_cat:
        cats_to_scan.append(("結構柱標籤", DB.BuiltInCategory.OST_StructuralColumnTags))

    to_delete = []
    summary_msg = []

    for cat_name, cat_enum in cats_to_scan:
        collector = DB.FilteredElementCollector(doc, active_view.Id) \
                      .OfCategory(cat_enum) \
                      .WhereElementIsNotElementType()

        cat_delete_ids = []
        for tag in collector:
            if isinstance(tag, DB.IndependentTag):
                text = tag.TagText or ""
                if "?" in text or "??" in text or text.strip() == "":
                    cat_delete_ids.append(tag.Id)

        to_delete.extend(cat_delete_ids)
        summary_msg.append("• {}: {} 個無效標籤".format(cat_name, len(cat_delete_ids)))

    if not to_delete:
        forms.alert("在當前視圖 [{}] 中沒有找到顯示 ?? 的無效標籤！".format(active_view.Name), title="掃描結果")
    else:
        total_count = len(to_delete)
        msg = "在當前視圖 [{}] 中自動掃描到以下無效標籤：\n\n{}\n\n總計: {} 個標籤。\n\n是否立即一鍵全自動刪除？".format(
            active_view.Name, "\n".join(summary_msg), total_count
        )

        if forms.alert(msg, title="一鍵清理確認", ok=True, cancel=True):
            with revit.Transaction("MCP: 自動清理無效標籤"):
                for tag_id in to_delete:
                    doc.Delete(tag_id)
            forms.alert("清理完成！共自動刪除了 {} 個無效標籤。".format(total_count), title="清理完成")
