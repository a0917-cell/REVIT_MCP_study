# -*- coding: utf-8 -*-
"""自動掃描當前視圖並刪除所有樑編號包含獨立 ? 或 (?) 的鋼樑標籤 (例如首行為 ? 且第二行為高程者)。"""

__title__ = '刪除\n樑編號為?'
__author__ = 'MCP'

from Autodesk.Revit import DB
from pyrevit import revit, forms

doc = revit.doc
active_view = doc.ActiveView

# 收集當前視圖中所有的結構構架標籤 (樑標籤)
collector = DB.FilteredElementCollector(doc, active_view.Id) \
              .OfCategory(DB.BuiltInCategory.OST_StructuralFramingTags) \
              .WhereElementIsNotElementType()

to_delete = []
for tag in collector:
    if isinstance(tag, DB.IndependentTag):
        text = tag.TagText or ""
        # 將多行文字拆分 (例如 第一行: ?, 第二行: -580)
        lines = [line.strip() for line in text.splitlines() if line.strip()]
        
        # 只要有任何一行的內容剛好是 "?" 或 "(?)" 或 "??"，即代表樑編號未設定/顯示為?
        if any(line in ["?", "(?)", "??", "(??)"] for line in lines):
            to_delete.append(tag.Id)

if not to_delete:
    forms.alert("在當前視圖 [{}] 中沒有找到樑編號顯示為 ? 的鋼樑標籤！".format(active_view.Name), title="掃描結果")
else:
    count = len(to_delete)
    msg = "在當前視圖 [{}] 中共自動掃描到 {} 個樑編號顯示為 ? 的鋼樑標籤。\n\n是否立即一鍵全自動刪除？".format(active_view.Name, count)

    if forms.alert(msg, title="一鍵刪除確認 (樑編號為?)", ok=True, cancel=True):
        with revit.Transaction("MCP: 刪除樑編號為 ? 的標籤"):
            for tag_id in to_delete:
                doc.Delete(tag_id)
        forms.alert("清理完成！共自動刪除了 {} 個樑編號顯示為 ? 的鋼樑標籤。".format(count), title="清理完成")
