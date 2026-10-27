using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private object DeleteQuestionMarkTags(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            IdType viewIdValue = parameters["viewId"]?.Value<IdType>() ?? 0;
            View targetView = viewIdValue == 0 ? doc.ActiveView : doc.GetElement(RevitCompatibility.ToElementId(viewIdValue)) as View;
            if (targetView == null) throw new Exception("無效的目標視圖");

            var categories = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_StructuralFramingTags,
                BuiltInCategory.OST_StructuralColumnTags
            };

            var toDelete = new List<ElementId>();
            foreach (var cat in categories)
            {
                var collector = new FilteredElementCollector(doc, targetView.Id)
                    .OfCategory(cat)
                    .WhereElementIsNotElementType();
                foreach (Element elem in collector)
                {
                    if (elem is IndependentTag tag)
                    {
                        string text = tag.TagText ?? "";
                        if (text.Contains("?") || string.IsNullOrWhiteSpace(text))
                        {
                            toDelete.Add(tag.Id);
                        }
                    }
                }
            }

            int deletedCount = 0;
            using (Transaction trans = new Transaction(doc, "MCP: 自動刪除無效標籤"))
            {
                trans.Start();
                foreach (var id in toDelete)
                {
                    doc.Delete(id);
                    deletedCount++;
                }
                trans.Commit();
            }

            return new { Success = true, DeletedCount = deletedCount, ScannedCount = toDelete.Count };
        }
    }
}
