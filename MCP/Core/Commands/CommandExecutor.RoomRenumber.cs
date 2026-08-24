using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core
{
    /// <summary>
    /// 房間重新排序編號（renumber_rooms_by_level）。
    /// 演算法規格見 domain/room-numbering-workflow.md — 由上到下、同列由左到右，
    /// 從 startNumber 的文字前綴與數字尾碼開始連續遞增，尾碼寬度保留。
    /// </summary>
    public partial class CommandExecutor
    {
        private const double ROOM_RENUMBER_FEET_TO_MM = 304.8;

        private sealed class RoomRenumberEntry
        {
            public Room Room;
            public double CenterXMm;
            public double CenterYMm;
            public string OldNumber;
            public string NewNumber;
            public bool Conflict;
        }

        private object RenumberRoomsByLevel(JObject parameters)
        {
            string levelName = parameters["level"]?.Value<string>();
            string startNumber = parameters["startNumber"]?.Value<string>();
            bool dryRun = parameters["dryRun"]?.Value<bool?>() ?? false;
            bool includeUnnamed = parameters["includeUnnamed"]?.Value<bool?>() ?? true;
            double yToleranceMm = parameters["yToleranceMm"]?.Value<double?>() ?? 3000.0;
            string parameterName = parameters["parameterName"]?.Value<string>();
            bool allowExistingNumberConflicts = parameters["allowExistingNumberConflicts"]?.Value<bool?>() ?? false;

            if (string.IsNullOrWhiteSpace(levelName))
                throw new Exception("必須提供 level");
            if (string.IsNullOrWhiteSpace(startNumber))
                throw new Exception("必須提供 startNumber");

            // 前綴 + 數字尾碼：尾碼取「所有結尾連續數字」，寬度以此為準（B134 -> 前綴 "B"、尾碼 "134"，寬度 3）。
            Match numberMatch = Regex.Match(startNumber, @"^(.*?)(\d+)$");
            if (!numberMatch.Success)
                throw new Exception($"startNumber '{startNumber}' 必須以數字結尾");
            string prefix = numberMatch.Groups[1].Value;
            int digitWidth = numberMatch.Groups[2].Value.Length;
            int startValue = int.Parse(numberMatch.Groups[2].Value);

            Document doc = _uiApp.ActiveUIDocument.Document;

            // SOP 要求樓層可唯一解析；找不到就報錯，不像其他工具那樣靜默退回第一個樓層。
            Level level = FindLevel(doc, levelName, false);

            var levelRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.LevelId == level.Id && r.Area > 0)
                .ToList();

            var candidates = new List<RoomRenumberEntry>();
            var skipped = new List<object>();

            foreach (var room in levelRooms)
            {
                string roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                if (!includeUnnamed && string.IsNullOrWhiteSpace(roomName))
                {
                    skipped.Add(new
                    {
                        ElementId = room.Id.GetIdValue(),
                        Number = GetRoomNumberValue(room, parameterName),
                        Reason = "unnamed room excluded (includeUnnamed=false)"
                    });
                    continue;
                }

                XYZ center = (room.Location as LocationPoint)?.Point;
                if (center == null)
                {
                    BoundingBoxXYZ bbox = room.get_BoundingBox(null);
                    if (bbox != null)
                        center = (bbox.Min + bbox.Max) * 0.5;
                }

                if (center == null)
                {
                    skipped.Add(new
                    {
                        ElementId = room.Id.GetIdValue(),
                        Number = GetRoomNumberValue(room, parameterName),
                        Reason = "no LocationPoint or BoundingBox available"
                    });
                    continue;
                }

                candidates.Add(new RoomRenumberEntry
                {
                    Room = room,
                    CenterXMm = center.X * ROOM_RENUMBER_FEET_TO_MM,
                    CenterYMm = center.Y * ROOM_RENUMBER_FEET_TO_MM,
                    OldNumber = GetRoomNumberValue(room, parameterName)
                });
            }

            // 由上到下（CenterY 由大到小）分列，容差內視為同列；同列內由左到右（CenterX 由小到大）。
            var byY = candidates.OrderByDescending(c => c.CenterYMm).ToList();
            var rows = new List<List<RoomRenumberEntry>>();
            foreach (var c in byY)
            {
                var currentRow = rows.Count > 0 ? rows[rows.Count - 1] : null;
                if (currentRow != null && Math.Abs(currentRow[0].CenterYMm - c.CenterYMm) <= yToleranceMm)
                    currentRow.Add(c);
                else
                    rows.Add(new List<RoomRenumberEntry> { c });
            }

            var sorted = new List<RoomRenumberEntry>();
            foreach (var row in rows)
                sorted.AddRange(row.OrderBy(c => c.CenterXMm));

            // 「已存在於目標樓層以外」的房號集合，用於偵測衝突（schema 定義即「outside the target level」）。
            var existingNumbersOutsideLevel = new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.LevelId != level.Id)
                    .Select(r => GetRoomNumberValue(r, parameterName))
                    .Where(n => !string.IsNullOrEmpty(n)));

            int value = startValue;
            var conflicts = new List<object>();
            foreach (var entry in sorted)
            {
                entry.NewNumber = prefix + value.ToString("D" + digitWidth);
                entry.Conflict = existingNumbersOutsideLevel.Contains(entry.NewNumber);
                if (entry.Conflict)
                {
                    conflicts.Add(new
                    {
                        ElementId = entry.Room.Id.GetIdValue(),
                        OldNumber = entry.OldNumber,
                        ProposedNumber = entry.NewNumber,
                        Reason = $"number '{entry.NewNumber}' already exists outside level '{level.Name}'"
                    });
                }
                value++;
            }

            string endNumber = sorted.Count > 0 ? sorted[sorted.Count - 1].NewNumber : startNumber;
            var roomsOut = sorted.Select(c => (object)new
            {
                ElementId = c.Room.Id.GetIdValue(),
                OldNumber = c.OldNumber,
                NewNumber = c.NewNumber,
                CenterX = Math.Round(c.CenterXMm, 1),
                CenterY = Math.Round(c.CenterYMm, 1),
                Conflict = c.Conflict
            }).ToList();

            bool blockedByConflicts = conflicts.Count > 0 && !allowExistingNumberConflicts;

            if (!dryRun && !blockedByConflicts && sorted.Count > 0)
            {
                using (Transaction trans = new Transaction(doc, "批次重新編號房間"))
                {
                    trans.Start();
                    var opts = trans.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new WarningSwallower());
                    trans.SetFailureHandlingOptions(opts);

                    foreach (var entry in sorted)
                        SetRoomNumberValue(entry.Room, parameterName, entry.NewNumber);

                    trans.Commit();
                }
            }

            return new
            {
                Level = level.Name,
                DryRun = dryRun,
                Written = !dryRun && !blockedByConflicts && sorted.Count > 0,
                Count = sorted.Count,
                StartNumber = startNumber,
                EndNumber = endNumber,
                Rooms = roomsOut,
                SkippedRooms = skipped,
                Conflicts = conflicts,
                Message = blockedByConflicts
                    ? "Conflicts detected outside the target level — no rooms were renumbered. Set allowExistingNumberConflicts=true to override, or choose a different startNumber."
                    : null
            };
        }

        /// <summary>
        /// 讀取房間編號：有指定 parameterName 就查該參數，否則用 Revit 內建房號 (Room.Number)。
        /// </summary>
        private static string GetRoomNumberValue(Room room, string parameterName)
        {
            if (!string.IsNullOrEmpty(parameterName))
                return room.LookupParameter(parameterName)?.AsString();
            return room.Number;
        }

        /// <summary>
        /// 寫入房間編號：有指定 parameterName 就寫該參數，否則寫 Revit 內建房號 (Room.Number)。
        /// </summary>
        private static void SetRoomNumberValue(Room room, string parameterName, string value)
        {
            if (!string.IsNullOrEmpty(parameterName))
            {
                var p = room.LookupParameter(parameterName);
                if (p == null)
                    throw new Exception($"房間 {room.Id.GetIdValue()} 找不到參數 '{parameterName}'");
                p.Set(value);
                return;
            }
            room.Number = value;
        }
    }
}
