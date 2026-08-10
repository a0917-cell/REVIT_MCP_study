using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
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
        /// <summary>
        /// 自動將結構構架（梁）端點延伸或切齊至鄰近結構構架邊緣、鋼柱邊緣或樓板邊緣。
        /// </summary>
        private object AlignStructuralFraming(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            // 1. 解析參數
            var framingIdsArray = parameters["framingIds"] as JArray;
            List<IdType> framingIds = framingIdsArray != null 
                ? framingIdsArray.Select(x => x.Value<IdType>()).ToList() 
                : new List<IdType>();

            var targetCategoryTypesArray = parameters["targetCategoryTypes"] as JArray;
            List<string> targetCategories = targetCategoryTypesArray != null
                ? targetCategoryTypesArray.Select(x => x.Value<string>().ToLower()).ToList()
                : new List<string> { "framing", "column", "floor", "wall" };

            double maxDistanceMm = parameters["maxDistanceMm"] != null ? parameters["maxDistanceMm"].Value<double>() : 2000.0;
            double maxDistanceFeet = maxDistanceMm / 304.8;

            bool disallowJoinAtEnds = parameters["disallowJoinAtEnds"] == null || parameters["disallowJoinAtEnds"].Value<bool>();

            IdType? viewId = parameters["viewId"]?.Value<IdType>();
            View targetView = viewId.HasValue 
                ? doc.GetElement(new ElementId(viewId.Value)) as View 
                : _uiApp.ActiveUIDocument.ActiveView;

            if (targetView == null)
            {
                return new { Success = false, Message = "無法取得有效的目標視圖" };
            }

            // 2. 收集要處理的結構構架 (Structural Framing)
            List<FamilyInstance> framingElements = new List<FamilyInstance>();
            if (framingIds.Count > 0)
            {
                foreach (var fid in framingIds)
                {
                    if (doc.GetElement(new ElementId(fid)) is FamilyInstance fi && 
                        fi.Category?.Id.GetIdValue() == (IdType)(int)BuiltInCategory.OST_StructuralFraming)
                    {
                        framingElements.Add(fi);
                    }
                }
            }
            else
            {
                // 若未指定 framingIds，則自動收集當前視圖中的所有結構構架
                framingElements = new FilteredElementCollector(doc, targetView.Id)
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .ToList();
            }

            if (framingElements.Count == 0)
            {
                return new { Success = true, TotalFramingProcessed = 0, AlignedEndCount = 0, Message = "當前範圍未找到任何結構構架 (Structural Framing)" };
            }

            // 3. 收集對齊目標邊界的候選元素
            List<Element> targetElements = new List<Element>();
            
            List<BuiltInCategory> catsToCollect = new List<BuiltInCategory>();
            if (targetCategories.Contains("framing")) catsToCollect.Add(BuiltInCategory.OST_StructuralFraming);
            if (targetCategories.Contains("column"))
            {
                catsToCollect.Add(BuiltInCategory.OST_StructuralColumns);
                catsToCollect.Add(BuiltInCategory.OST_Columns);
            }
            if (targetCategories.Contains("floor")) catsToCollect.Add(BuiltInCategory.OST_Floors);
            if (targetCategories.Contains("wall")) catsToCollect.Add(BuiltInCategory.OST_Walls);

            foreach (var cat in catsToCollect)
            {
                var elems = new FilteredElementCollector(doc, targetView.Id)
                    .OfCategory(cat)
                    .WhereElementIsNotElementType()
                    .ToElements();
                targetElements.AddRange(elems);
            }

            // 4. 幾何運算與對齊處理
            int alignedEndCount = 0;
            List<object> details = new List<object>();

            // 建立 3D 幾何擷取選項（不設 View 以獲取完整 3D Solid 面）
            Options geomOpt3D = new Options
            {
                ComputeReferences = true,
                DetailLevel = ViewDetailLevel.Fine
            };

            using (Transaction trans = TransactionHelper.Begin(doc, "Align Structural Framing Endpoints"))
            {
                trans.Start();

                foreach (var framing in framingElements)
                {
                    if (!(framing.Location is LocationCurve locCurve) || !(locCurve.Curve is Line beamLine))
                    {
                        continue;
                    }

                    XYZ p0 = beamLine.GetEndPoint(0);
                    XYZ p1 = beamLine.GetEndPoint(1);
                    XYZ v = (p1 - p0).Normalize();

                    XYZ newP0 = p0;
                    XYZ newP1 = p1;

                    bool p0Changed = false;
                    bool p1Changed = false;

                    // 處理 End 0 (向 -V 方向延伸或切齊)
                    var res0 = FindFramingTargetIntersection(doc, framing, p0, -v, targetElements, maxDistanceFeet, geomOpt3D);
                    if (res0 != null)
                    {
                        newP0 = res0.Value.IntersectionPoint;
                        p0Changed = true;
                        alignedEndCount++;
                        if (disallowJoinAtEnds)
                        {
                            try 
                            { 
                                StructuralFramingUtils.DisallowJoinAtEnd(framing, 0); 
                                Parameter param0 = framing.get_Parameter(BuiltInParameter.START_EXTENSION) ?? framing.LookupParameter("起點延伸");
                                if (param0 != null && !param0.IsReadOnly) param0.Set(0.0);
                            } 
                            catch { }
                        }
                        details.Add(new {
                            FramingId = framing.Id.GetIdValue(),
                            End = 0,
                            TargetId = res0.Value.TargetId,
                            TargetCategory = res0.Value.TargetCategory,
                            AdjustmentMm = Math.Round(res0.Value.DistanceFeet * 304.8, 1)
                        });
                    }

                    // 處理 End 1 (向 +V 方向延伸或切齊)
                    var res1 = FindFramingTargetIntersection(doc, framing, p1, v, targetElements, maxDistanceFeet, geomOpt3D);
                    if (res1 != null)
                    {
                        newP1 = res1.Value.IntersectionPoint;
                        p1Changed = true;
                        alignedEndCount++;
                        if (disallowJoinAtEnds)
                        {
                            try 
                            { 
                                StructuralFramingUtils.DisallowJoinAtEnd(framing, 1); 
                                Parameter param1 = framing.get_Parameter(BuiltInParameter.END_EXTENSION) ?? framing.LookupParameter("終點延伸");
                                if (param1 != null && !param1.IsReadOnly) param1.Set(0.0);
                            } 
                            catch { }
                        }
                        details.Add(new {
                            FramingId = framing.Id.GetIdValue(),
                            End = 1,
                            TargetId = res1.Value.TargetId,
                            TargetCategory = res1.Value.TargetCategory,
                            AdjustmentMm = Math.Round(res1.Value.DistanceFeet * 304.8, 1)
                        });
                    }

                    if (p0Changed || p1Changed)
                    {
                        try
                        {
                            // 更新 LocationCurve 軸線
                            locCurve.Curve = Line.CreateBound(newP0, newP1);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[AlignFraming] Failed to update LocationCurve for Framing ID={framing.Id.GetIdValue()}: {ex.Message}");
                        }
                    }
                }

                trans.Commit();
            }

            return new
            {
                Success = true,
                TotalFramingProcessed = framingElements.Count,
                AlignedEndCount = alignedEndCount,
                Details = details,
                Message = $"成功完成 {framingElements.Count} 支結構構架之對齊處理，共延伸/切齊 {alignedEndCount} 個端點。"
            };
        }

        private struct FramingIntersectionResult
        {
            public XYZ IntersectionPoint;
            public IdType TargetId;
            public string TargetCategory;
            public double DistanceFeet;
        }

        private FramingIntersectionResult? FindFramingTargetIntersection(
            Document doc,
            FamilyInstance currentFraming,
            XYZ endPoint,
            XYZ rayDir,
            List<Element> targetElements,
            double maxDistanceFeet,
            Options geomOpt3D)
        {
            FramingIntersectionResult? closestResult = null;
            double minDistance = double.MaxValue;

            double offsetBackFeet = 100.0 / 304.8;
            XYZ rayStart = endPoint - rayDir * offsetBackFeet;
            XYZ rayEnd = endPoint + rayDir * maxDistanceFeet;
            Curve rayLine = Line.CreateBound(rayStart, rayEnd);

            foreach (var target in targetElements)
            {
                if (target.Id.GetIdValue() == currentFraming.Id.GetIdValue()) continue;

                BoundingBoxXYZ bbox = target.get_BoundingBox(null);
                if (bbox == null) continue;

                GeometryElement geomElem = target.get_Geometry(geomOpt3D);
                if (geomElem != null)
                {
                    List<Face> faces = GetAllFramingTargetFaces(geomElem);
                    foreach (var face in faces)
                    {
                        IntersectionResultArray intersectRes;
                        SetComparisonResult comp = face.Intersect(rayLine, out intersectRes);
                        if (comp == SetComparisonResult.Overlap || comp == SetComparisonResult.Subset)
                        {
                            if (intersectRes != null && !intersectRes.IsEmpty)
                            {
                                foreach (IntersectionResult ir in intersectRes)
                                {
                                    XYZ hitPt = ir.XYZPoint;
                                    double distFeet = (hitPt - endPoint).DotProduct(rayDir);

                                    if (distFeet >= -offsetBackFeet && distFeet <= maxDistanceFeet)
                                    {
                                        double absDist = Math.Abs(distFeet);
                                        if (absDist < minDistance && absDist > 0.001)
                                        {
                                            minDistance = absDist;
                                            closestResult = new FramingIntersectionResult
                                            {
                                                IntersectionPoint = hitPt,
                                                TargetId = target.Id.GetIdValue(),
                                                TargetCategory = target.Category?.Name ?? "Target",
                                                DistanceFeet = distFeet
                                            };
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (closestResult == null)
                {
                    XYZ bMin = bbox.Min;
                    XYZ bMax = bbox.Max;

                    List<Line> bbox2DLines = new List<Line>
                    {
                        Line.CreateBound(new XYZ(bMin.X, bMin.Y, endPoint.Z), new XYZ(bMax.X, bMin.Y, endPoint.Z)),
                        Line.CreateBound(new XYZ(bMax.X, bMin.Y, endPoint.Z), new XYZ(bMax.X, bMax.Y, endPoint.Z)),
                        Line.CreateBound(new XYZ(bMax.X, bMax.Y, endPoint.Z), new XYZ(bMin.X, bMax.Y, endPoint.Z)),
                        Line.CreateBound(new XYZ(bMin.X, bMax.Y, endPoint.Z), new XYZ(bMin.X, bMin.Y, endPoint.Z))
                    };

                    foreach (var bLine in bbox2DLines)
                    {
                        IntersectionResultArray irArray;
                        SetComparisonResult comp = bLine.Intersect(rayLine, out irArray);
                        if (comp == SetComparisonResult.Overlap || comp == SetComparisonResult.Subset)
                        {
                            if (irArray != null && !irArray.IsEmpty)
                            {
                                foreach (IntersectionResult ir in irArray)
                                {
                                    XYZ hitPt = ir.XYZPoint;
                                    double distFeet = (hitPt - endPoint).DotProduct(rayDir);
                                    if (distFeet >= -offsetBackFeet && distFeet <= maxDistanceFeet)
                                    {
                                        double absDist = Math.Abs(distFeet);
                                        if (absDist < minDistance && absDist > 0.001)
                                        {
                                            minDistance = absDist;
                                            closestResult = new FramingIntersectionResult
                                            {
                                                IntersectionPoint = hitPt,
                                                TargetId = target.Id.GetIdValue(),
                                                TargetCategory = target.Category?.Name ?? "Target",
                                                DistanceFeet = distFeet
                                            };
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return closestResult;
        }

        private List<Face> GetAllFramingTargetFaces(GeometryElement geomElem)
        {
            List<Face> faces = new List<Face>();
            foreach (GeometryObject obj in geomElem)
            {
                if (obj is Solid solid && solid.Volume > 1e-6)
                {
                    foreach (Face face in solid.Faces)
                    {
                        faces.Add(face);
                    }
                }
                else if (obj is GeometryInstance inst)
                {
                    GeometryElement instGeom = inst.GetInstanceGeometry();
                    if (instGeom != null)
                    {
                        faces.AddRange(GetAllFramingTargetFaces(instGeom));
                    }
                }
            }
            return faces;
        }
    }
}
