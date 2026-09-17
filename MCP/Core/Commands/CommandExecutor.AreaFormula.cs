using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;

// Revit 2025+ ElementId: int → long
#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// 送照樓板面積計算式命令。
    ///
    /// 方法定義於 domain/permit-floor-area-formula.md，本檔案是其唯一 C# 落地：
    /// - 只「消費」使用者已用區域邊界線／房間分隔線切好的封閉區域，不自行分解多邊形
    /// - 每個區域的外環必須化簡成矩形（四頂點、四直角）或三角形，否則列入 Undecomposed 跳過
    /// - 計算式印的是「四捨五入後的邊長」乘積，因為送審圖上的算式必須自洽
    /// - 每一項都拿該乘積與 shoelace 實際幾何面積對帳，超過容差列入 Mismatches
    ///
    /// 對帳這一步是刻意的：只印算式而不驗證，等於產出一份看起來像通過、
    /// 但從未跟幾何比對過的文件。
    ///
    /// 純幾何邏輯（化簡／分類／格式化）抽成 static 方法，只吃 double 陣列不吃 Revit 型別。
    /// </summary>
    public partial class CommandExecutor
    {
        #region 送照樓板面積計算式

        // FeetToMm 已宣告在 CommandExecutor.RoomFinish.cs（同一個 partial class），此處不再重複宣告。

        /// <summary>單一區域的判定結果。Shape 為 rectangle / triangle / undecomposed。</summary>
        internal struct AreaFormulaShape
        {
            public string Shape;
            /// <summary>矩形：[長, 寬]；三角形：[底, 高]。單位 m，未四捨五入。</summary>
            public double[] DimensionsM;
            /// <summary>外環化簡後的頂點數，用於回報無法分解的原因。</summary>
            public int VertexCount;
            public string Reason;
        }

        /// <summary>
        /// 化簡多邊形頂點：移除重複點與共線點。
        /// 輸入輸出皆為 mm 的 [x, y] 陣列，不依賴 Revit 型別。
        ///
        /// 共線點一定要移除：一道被切成兩段的牆會產生兩個 BoundarySegment，
        /// 不化簡的話一個標準矩形會有 5、6 個頂點，直接被誤判成無法分解。
        /// </summary>
        internal static List<double[]> SimplifyPolygon(List<double[]> points, double duplicateToleranceMm, double collinearToleranceMm)
        {
            var cleaned = new List<double[]>();
            if (points == null || points.Count == 0)
                return cleaned;

            foreach (var p in points)
            {
                if (cleaned.Count == 0)
                {
                    cleaned.Add(p);
                    continue;
                }
                var last = cleaned[cleaned.Count - 1];
                if (Distance(last, p) > duplicateToleranceMm)
                    cleaned.Add(p);
            }

            // 頭尾若重合，移除尾點（環狀多邊形不重複記錄起點）
            while (cleaned.Count > 1 && Distance(cleaned[0], cleaned[cleaned.Count - 1]) <= duplicateToleranceMm)
                cleaned.RemoveAt(cleaned.Count - 1);

            if (cleaned.Count < 3)
                return cleaned;

            // 反覆移除共線點，直到沒有可移除的為止（一次掃描可能因刪除而讓新的相鄰三點共線）
            bool removed = true;
            while (removed && cleaned.Count > 3)
            {
                removed = false;
                for (int i = 0; i < cleaned.Count; i++)
                {
                    var prev = cleaned[(i - 1 + cleaned.Count) % cleaned.Count];
                    var cur = cleaned[i];
                    var next = cleaned[(i + 1) % cleaned.Count];

                    double baseLength = Distance(prev, next);
                    if (baseLength <= duplicateToleranceMm)
                        continue;

                    // 點到 prev-next 直線的垂直距離
                    double cross = Math.Abs((next[0] - prev[0]) * (cur[1] - prev[1]) - (next[1] - prev[1]) * (cur[0] - prev[0]));
                    double deviation = cross / baseLength;

                    if (deviation <= collinearToleranceMm)
                    {
                        cleaned.RemoveAt(i);
                        removed = true;
                        break;
                    }
                }
            }

            return cleaned;
        }

        private static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0];
            double dy = a[1] - b[1];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Shoelace 公式求多邊形面積，輸入 mm，回傳 m²。純函式。</summary>
        internal static double PolygonAreaM2(List<double[]> points)
        {
            if (points == null || points.Count < 3)
                return 0;

            double sum = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return Math.Abs(sum) / 2.0 / 1000000.0;
        }

        /// <summary>
        /// 判定化簡後的多邊形是矩形還是三角形，並取出計算式要用的兩個邊長（m）。
        /// 四頂點且四個內角都落在 90° ± angleToleranceDeg 才算矩形；三頂點一律視為三角形，
        /// 底取最長邊、高由 2×面積÷底 反算（這樣任何三角形都成立，不必是直角三角形）。
        /// 純函式。
        /// </summary>
        internal static AreaFormulaShape ClassifyPolygon(List<double[]> points, double angleToleranceDeg)
        {
            var result = new AreaFormulaShape
            {
                Shape = "undecomposed",
                DimensionsM = null,
                VertexCount = points == null ? 0 : points.Count,
                Reason = null
            };

            if (points == null || points.Count < 3)
            {
                result.Reason = "外環化簡後不足三個頂點，無法構成封閉區域。";
                return result;
            }

            if (points.Count == 3)
            {
                double areaM2 = PolygonAreaM2(points);
                if (areaM2 <= 0)
                {
                    result.Reason = "三角形面積為 0，三點共線或幾何異常。";
                    return result;
                }

                // 直角三角形直接取夾直角的兩股當底與高 —— 那才是送審圖上會寫的形式，
                // 且算式精確。用最長邊當底、由面積反算高，會把 6×3÷2 寫成 6.71×2.68÷2。
                for (int i = 0; i < 3; i++)
                {
                    var prev = points[(i - 1 + 3) % 3];
                    var cur = points[i];
                    var next = points[(i + 1) % 3];

                    double ux = prev[0] - cur[0], uy = prev[1] - cur[1];
                    double vx = next[0] - cur[0], vy = next[1] - cur[1];
                    double lu = Math.Sqrt(ux * ux + uy * uy);
                    double lv = Math.Sqrt(vx * vx + vy * vy);
                    if (lu <= 0 || lv <= 0)
                        continue;

                    double cosAngle = (ux * vx + uy * vy) / (lu * lv);
                    cosAngle = Math.Max(-1.0, Math.Min(1.0, cosAngle));
                    double angle = Math.Acos(cosAngle) * 180.0 / Math.PI;
                    if (Math.Abs(angle - 90.0) <= angleToleranceDeg)
                    {
                        result.Shape = "triangle";
                        result.DimensionsM = new[] { lu / 1000.0, lv / 1000.0 };
                        return result;
                    }
                }

                // 非直角三角形：底取最長邊，高由 2×面積÷底 反算
                double s0 = Distance(points[0], points[1]) / 1000.0;
                double s1 = Distance(points[1], points[2]) / 1000.0;
                double s2 = Distance(points[2], points[0]) / 1000.0;
                double baseM = Math.Max(s0, Math.Max(s1, s2));
                if (baseM <= 0)
                {
                    result.Reason = "三角形最長邊為 0，幾何異常。";
                    return result;
                }
                result.Shape = "triangle";
                result.DimensionsM = new[] { baseM, 2.0 * areaM2 / baseM };
                return result;
            }

            if (points.Count == 4)
            {
                bool allRight = true;
                for (int i = 0; i < 4; i++)
                {
                    var prev = points[(i - 1 + 4) % 4];
                    var cur = points[i];
                    var next = points[(i + 1) % 4];

                    double ux = prev[0] - cur[0], uy = prev[1] - cur[1];
                    double vx = next[0] - cur[0], vy = next[1] - cur[1];
                    double lu = Math.Sqrt(ux * ux + uy * uy);
                    double lv = Math.Sqrt(vx * vx + vy * vy);
                    if (lu <= 0 || lv <= 0)
                    {
                        allRight = false;
                        break;
                    }
                    double cos = (ux * vx + uy * vy) / (lu * lv);
                    cos = Math.Max(-1.0, Math.Min(1.0, cos));
                    double angleDeg = Math.Acos(cos) * 180.0 / Math.PI;
                    if (Math.Abs(angleDeg - 90.0) > angleToleranceDeg)
                    {
                        allRight = false;
                        break;
                    }
                }

                if (allRight)
                {
                    result.Shape = "rectangle";
                    result.DimensionsM = new[]
                    {
                        Distance(points[0], points[1]) / 1000.0,
                        Distance(points[1], points[2]) / 1000.0
                    };
                    return result;
                }

                result.Reason = "四頂點但有內角不是直角（非矩形四邊形），請再切成矩形與三角形。";
                return result;
            }

            result.Reason = $"外環化簡後有 {points.Count} 個頂點，不是矩形或三角形，請先用區域邊界線切分。";
            return result;
        }

        /// <summary>四捨五入（不是銀行家捨入）。送審算式用的是一般四捨五入。</summary>
        private static double RoundHalfUp(double value, int decimals)
        {
            return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 依形狀與四捨五入後的邊長組出算式文字與其乘積。
        /// 回傳的 areaM2 是「印在圖上的那個算式算出來的值」，不是幾何真值 —— 兩者的差就是要對帳的東西。
        /// 純函式。
        /// </summary>
        internal static void BuildFormulaTerm(string shape, double[] dimensionsM, int decimals, out string expression, out double areaM2)
        {
            string fmt = "F" + decimals.ToString(CultureInfo.InvariantCulture);
            double a = RoundHalfUp(dimensionsM[0], decimals);
            double b = RoundHalfUp(dimensionsM[1], decimals);

            if (shape == "triangle")
            {
                areaM2 = RoundHalfUp(a * b / 2.0, decimals);
                expression = a.ToString(fmt, CultureInfo.InvariantCulture) + "×" +
                             b.ToString(fmt, CultureInfo.InvariantCulture) + "÷2 = " +
                             areaM2.ToString(fmt, CultureInfo.InvariantCulture);
                return;
            }

            areaM2 = RoundHalfUp(a * b, decimals);
            expression = a.ToString(fmt, CultureInfo.InvariantCulture) + "×" +
                         b.ToString(fmt, CultureInfo.InvariantCulture) + " = " +
                         areaM2.ToString(fmt, CultureInfo.InvariantCulture);
        }

        private static SpatialElementBoundaryLocation ParseBoundaryLocation(string value)
        {
            switch ((value ?? "center").Trim().ToLowerInvariant())
            {
                case "finish": return SpatialElementBoundaryLocation.Finish;
                case "corecenter": return SpatialElementBoundaryLocation.CoreCenter;
                case "coreboundary": return SpatialElementBoundaryLocation.CoreBoundary;
                case "center": return SpatialElementBoundaryLocation.Center;
                default:
                    throw new Exception($"boundaryLocation 只接受 center / finish / coreCenter / coreBoundary，收到 '{value}'。");
            }
        }

        /// <summary>
        /// 取出空間元素外環的頂點（mm）。任一段不是直線（弧、雲形線）就回傳 false，
        /// 呼叫端應列入 Undecomposed —— 弧邊沒有「長×寬」可言。
        /// </summary>
        private static bool TryGetOuterLoopPoints(SpatialElement spatial, SpatialElementBoundaryOptions options, out List<double[]> points, out string reason)
        {
            points = new List<double[]>();
            reason = null;

            IList<IList<BoundarySegment>> loops = spatial.GetBoundarySegments(options);
            if (loops == null || loops.Count == 0 || loops[0].Count == 0)
            {
                reason = "取不到邊界線（未放置或邊界未封閉）。";
                return false;
            }

            foreach (BoundarySegment segment in loops[0])
            {
                Curve curve = segment.GetCurve();
                if (!(curve is Line))
                {
                    reason = "邊界含非直線段（弧或雲形線），無法化為長×寬算式。";
                    return false;
                }
                XYZ start = curve.GetEndPoint(0);
                points.Add(new[] { start.X * FeetToMm, start.Y * FeetToMm });
            }

            return true;
        }

        /// <summary>
        /// 產出送照樓板面積計算式，並可寫成圖面 TextNote。
        /// </summary>
        private object CreateFloorAreaFormula(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            IdType? viewIdParam = parameters["viewId"]?.Value<IdType?>();
            View view = viewIdParam.HasValue
                ? doc.GetElement(new ElementId(viewIdParam.Value)) as View
                : doc.ActiveView;
            if (view == null)
                throw new Exception(viewIdParam.HasValue ? $"找不到視圖 ID: {viewIdParam.Value}" : "取不到作用中的視圖。");

            string sourceType = (parameters["sourceType"]?.Value<string>() ?? "auto").Trim().ToLowerInvariant();
            SpatialElementBoundaryOptions boundaryOptions = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = ParseBoundaryLocation(parameters["boundaryLocation"]?.Value<string>())
            };
            string label = parameters["label"]?.Value<string>();
            string itemPrefix = parameters["itemPrefix"]?.Value<string>() ?? "";
            int decimals = parameters["decimals"]?.Value<int?>() ?? 2;
            double angleToleranceDeg = parameters["angleToleranceDeg"]?.Value<double?>() ?? 1.0;
            double areaToleranceM2 = parameters["areaToleranceM2"]?.Value<double?>() ?? 0.05;
            string areaUnitLabel = parameters["areaUnitLabel"]?.Value<string>() ?? "㎡";
            string textTypeName = parameters["textTypeName"]?.Value<string>();
            bool dryRun = parameters["dryRun"]?.Value<bool?>() ?? false;

            if (decimals < 0 || decimals > 6)
                throw new Exception("decimals 必須介於 0 到 6。");
            if (areaToleranceM2 <= 0)
                throw new Exception("areaToleranceM2 必須大於 0：四捨五入本身就會產生差異，容差為 0 會讓每一項都被判為不符。");

            // 1. 收集來源空間元素
            var sourceIdsToken = parameters["sourceIds"] as JArray;
            var spatials = new List<SpatialElement>();
            string sourceScope;

            if (sourceIdsToken != null && sourceIdsToken.Count > 0)
            {
                foreach (var token in sourceIdsToken)
                {
                    IdType rawId = token.Value<IdType>();
                    Element element = doc.GetElement(new ElementId(rawId));
                    SpatialElement spatial = element as SpatialElement;
                    if (spatial == null)
                        throw new Exception($"ElementId {rawId} 不是 Area 或 Room（實際型別：{element?.GetType().Name ?? "找不到元件"}）。");
                    spatials.Add(spatial);
                }
                sourceScope = "sourceIds";
            }
            else
            {
                spatials = CollectSpatialElements(doc, view, sourceType, out sourceScope);
                // 未指定順序時，依上到下、左到右排序，與圖面閱讀順序一致
                var orderInput = new List<RoomOrderPoint>();
                var byId = new Dictionary<string, SpatialElement>();
                foreach (SpatialElement spatial in spatials)
                {
                    double xMm, yMm;
                    if (!TryGetSpatialCenterMm(spatial, out xMm, out yMm))
                        continue;
                    string key = spatial.Id.GetIdValue().ToString(CultureInfo.InvariantCulture);
                    byId[key] = spatial;
                    orderInput.Add(new RoomOrderPoint { Id = key, X = xMm, Y = yMm });
                }
                var ordered = OrderRoomsTopDownLeftRight(orderInput, 3000);
                spatials = ordered.Select(p => byId[p.Id]).ToList();
            }

            if (spatials.Count == 0)
                throw new Exception($"在視圖「{view.Name}」找不到可用的 Area 或 Room（收集範圍：{sourceScope}）。請先放置區域／房間，或用 sourceIds 明確指定。");

            // 2. 逐區判形、組算式、對帳
            var items = new List<object>();
            var undecomposed = new List<object>();
            var mismatches = new List<object>();
            var formulaLines = new List<string>();
            string fmt = "F" + decimals.ToString(CultureInfo.InvariantCulture);
            double total = 0;
            int itemIndex = 0;

            foreach (SpatialElement spatial in spatials)
            {
                IdType elementId = spatial.Id.GetIdValue();
                string name = spatial.Name;

                List<double[]> rawPoints;
                string reason;
                if (!TryGetOuterLoopPoints(spatial, boundaryOptions, out rawPoints, out reason))
                {
                    undecomposed.Add(new { ElementId = elementId, Name = name, Reason = reason, VertexCount = 0 });
                    continue;
                }

                List<double[]> simplified = SimplifyPolygon(rawPoints, 1.0, 1.0);
                AreaFormulaShape shape = ClassifyPolygon(simplified, angleToleranceDeg);

                if (shape.Shape == "undecomposed")
                {
                    undecomposed.Add(new
                    {
                        ElementId = elementId,
                        Name = name,
                        Reason = shape.Reason,
                        VertexCount = shape.VertexCount
                    });
                    continue;
                }

                string expression;
                double formulaAreaM2;
                BuildFormulaTerm(shape.Shape, shape.DimensionsM, decimals, out expression, out formulaAreaM2);

                double geometricAreaM2 = PolygonAreaM2(simplified);
                double delta = Math.Abs(formulaAreaM2 - geometricAreaM2);

                itemIndex++;
                string itemLabel = string.IsNullOrEmpty(itemPrefix)
                    ? ""
                    : itemPrefix + itemIndex.ToString(CultureInfo.InvariantCulture);

                string line = string.IsNullOrEmpty(itemLabel) ? expression : itemLabel + "  " + expression;
                formulaLines.Add(line);
                total += formulaAreaM2;

                items.Add(new
                {
                    ElementId = elementId,
                    Name = name,
                    ItemLabel = string.IsNullOrEmpty(itemLabel) ? null : itemLabel,
                    Shape = shape.Shape,
                    DimensionsM = shape.DimensionsM.Select(d => RoundHalfUp(d, decimals)).ToArray(),
                    Expression = expression,
                    FormulaAreaM2 = formulaAreaM2,
                    GeometricAreaM2 = Math.Round(geometricAreaM2, 4),
                    DeltaM2 = Math.Round(delta, 4),
                    WithinTolerance = delta <= areaToleranceM2
                });

                if (delta > areaToleranceM2)
                {
                    mismatches.Add(new
                    {
                        ElementId = elementId,
                        Name = name,
                        Expression = expression,
                        FormulaAreaM2 = formulaAreaM2,
                        GeometricAreaM2 = Math.Round(geometricAreaM2, 4),
                        DeltaM2 = Math.Round(delta, 4),
                        Reason = "算式乘積與實際幾何面積差異超過容差，請確認該區域是否真的是矩形／三角形。"
                    });
                }
            }

            double totalRounded = RoundHalfUp(total, decimals);

            // 3. 組出完整文字
            var textBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(label))
                textBuilder.AppendLine(label);
            foreach (string line in formulaLines)
                textBuilder.AppendLine(line);
            textBuilder.Append("合計 = " + totalRounded.ToString(fmt, CultureInfo.InvariantCulture) + " " + areaUnitLabel);
            string formulaText = textBuilder.ToString();

            // 4. 寫入圖面
            IdType? textNoteId = null;
            string usedTextTypeName = null;
            bool written = false;

            if (!dryRun)
            {
                if (items.Count == 0)
                    throw new Exception("沒有任何區域能化為算式，未建立 TextNote。請先看 Undecomposed 的原因並切分區域。");

                double? x = parameters["x"]?.Value<double?>();
                double? y = parameters["y"]?.Value<double?>();
                if (!x.HasValue || !y.HasValue)
                    throw new Exception("dryRun=false 時必須指定 x 與 y（mm，專案座標）作為 TextNote 放置點。");

                TextNoteType textType = ResolveTextNoteType(doc, textTypeName);
                usedTextTypeName = textType.Name;

                using (Transaction trans = TransactionHelper.Begin(doc, "建立送照樓板面積計算式"))
                {
                    trans.Start();
                    try
                    {
                        var options = new TextNoteOptions
                        {
                            TypeId = textType.Id,
                            HorizontalAlignment = HorizontalTextAlignment.Left
                        };
                        TextNote note = TextNote.Create(doc, view.Id, new XYZ(x.Value / FeetToMm, y.Value / FeetToMm, 0), formulaText, options);
                        textNoteId = note.Id.GetIdValue();
                        trans.Commit();
                        written = true;
                    }
                    catch (Exception ex)
                    {
                        if (trans.GetStatus() == TransactionStatus.Started)
                            trans.RollBack();
                        throw new Exception($"建立計算式 TextNote 失敗，已回滾: {ex.Message}", ex);
                    }
                }
            }

            string message;
            if (dryRun)
                message = $"Dry-run：{items.Count} 項已列入計算式，合計 {totalRounded.ToString(fmt, CultureInfo.InvariantCulture)} {areaUnitLabel}；{undecomposed.Count} 個區域無法分解，{mismatches.Count} 項對帳不符。尚未寫入圖面。";
            else
                message = $"已在視圖「{view.Name}」建立計算式：{items.Count} 項，合計 {totalRounded.ToString(fmt, CultureInfo.InvariantCulture)} {areaUnitLabel}；{undecomposed.Count} 個區域無法分解，{mismatches.Count} 項對帳不符。";

            return new
            {
                ViewId = view.Id.GetIdValue(),
                ViewName = view.Name,
                SourceScope = sourceScope,
                Count = items.Count,
                TotalAreaM2 = totalRounded,
                AreaUnitLabel = areaUnitLabel,
                FormulaText = formulaText,
                Items = items,
                Undecomposed = undecomposed,
                Mismatches = mismatches,
                DryRun = dryRun,
                Written = written,
                TextNoteId = textNoteId,
                TextNoteTypeName = usedTextTypeName,
                Message = message
            };
        }

        /// <summary>
        /// 依 sourceType 收集空間元素。先試視圖範圍（Area 只在 AreaPlan 看得到），
        /// 視圖範圍取不到就退回該視圖所屬樓層 —— 兩種來源都會在 scope 回報，不會靜默換來源。
        /// </summary>
        private static List<SpatialElement> CollectSpatialElements(Document doc, View view, string sourceType, out string scope)
        {
            var result = new List<SpatialElement>();
            scope = "none";

            bool wantArea = sourceType == "auto" || sourceType == "area";
            bool wantRoom = sourceType == "auto" || sourceType == "room";

            if (wantArea)
            {
                result = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Areas)
                    .WhereElementIsNotElementType()
                    .OfType<SpatialElement>()
                    .Where(s => s.Area > 0)
                    .ToList();
                if (result.Count > 0)
                {
                    scope = "view:areas";
                    return result;
                }
            }

            if (wantRoom)
            {
                result = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .OfType<SpatialElement>()
                    .Where(s => s.Area > 0)
                    .ToList();
                if (result.Count > 0)
                {
                    scope = "view:rooms";
                    return result;
                }

                Level genLevel = view.GenLevel;
                if (genLevel != null)
                {
                    result = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .Where(r => r.LevelId == genLevel.Id && r.Area > 0)
                        .OfType<SpatialElement>()
                        .ToList();
                    if (result.Count > 0)
                    {
                        scope = "level:rooms";
                        return result;
                    }
                }
            }

            return new List<SpatialElement>();
        }

        /// <summary>取空間元素中心點（mm）：優先 LocationPoint，其次 BoundingBox 中心。</summary>
        private static bool TryGetSpatialCenterMm(SpatialElement spatial, out double xMm, out double yMm)
        {
            LocationPoint locationPoint = spatial.Location as LocationPoint;
            if (locationPoint != null)
            {
                xMm = locationPoint.Point.X * FeetToMm;
                yMm = locationPoint.Point.Y * FeetToMm;
                return true;
            }

            BoundingBoxXYZ bbox = spatial.get_BoundingBox(null);
            if (bbox != null)
            {
                xMm = (bbox.Min.X + bbox.Max.X) / 2.0 * FeetToMm;
                yMm = (bbox.Min.Y + bbox.Max.Y) / 2.0 * FeetToMm;
                return true;
            }

            xMm = 0;
            yMm = 0;
            return false;
        }

        /// <summary>依名稱找 TextNoteType；沒給名稱就取專案第一個。找不到指定名稱一律報錯，不靜默退回。</summary>
        private static TextNoteType ResolveTextNoteType(Document doc, string textTypeName)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .ToList();

            if (types.Count == 0)
                throw new Exception("專案中找不到任何 TextNoteType。");

            if (string.IsNullOrWhiteSpace(textTypeName))
                return types[0];

            TextNoteType match = types.FirstOrDefault(t => t.Name == textTypeName);
            if (match == null)
            {
                string available = string.Join(", ", types.Select(t => t.Name).Take(20));
                throw new Exception($"找不到名為 '{textTypeName}' 的 TextNoteType。可用類型（最多列 20 個）：{available}");
            }
            return match;
        }

        #endregion
    }
}
