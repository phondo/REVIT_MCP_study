using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// 避難路徑檢核服務 (Egress Audit Service)
    /// </summary>
    public class EgressAuditService
    {
        // 固定共用參數 GUID
        public static readonly Guid GuidActualDist = new Guid("A1B2C3D4-1111-4000-8000-000000000001");
        public static readonly Guid GuidMaxAllowed = new Guid("A1B2C3D4-2222-4000-8000-000000000002");
        public static readonly Guid GuidIsCompliant = new Guid("A1B2C3D4-3333-4000-8000-000000000003");
        public static readonly Guid GuidTargetExit = new Guid("A1B2C3D4-4444-4000-8000-000000000004");

        // 固定共用參數名稱
        public const string ParamActualDist = "Egress_Actual_Dist";
        public const string ParamMaxAllowed = "Egress_Max_Allowed";
        public const string ParamIsCompliant = "Egress_Is_Compliant";
        public const string ParamTargetExit = "Egress_Target_Exit";

        // 8 方位單位向量（供頂點向心內縮失敗時的放射搜尋備援）
        private static readonly XYZ[] EightDirections = new XYZ[]
        {
            new XYZ(1, 0, 0),
            new XYZ(Math.Sqrt(0.5), Math.Sqrt(0.5), 0),
            new XYZ(0, 1, 0),
            new XYZ(-Math.Sqrt(0.5), Math.Sqrt(0.5), 0),
            new XYZ(-1, 0, 0),
            new XYZ(-Math.Sqrt(0.5), -Math.Sqrt(0.5), 0),
            new XYZ(0, -1, 0),
            new XYZ(Math.Sqrt(0.5), -Math.Sqrt(0.5), 0)
        };

        private class EgressSharedParamSpec
        {
            public string Name { get; set; } = string.Empty;
            public Guid Guid { get; set; }
            public ForgeTypeId DataType { get; set; } = SpecTypeId.String.Text;
            public ForgeTypeId GroupType { get; set; } = GroupTypeId.Data;
        }

        private class ExitDoorTarget
        {
            public long DoorId { get; set; }
            public ElementId ElementId { get; set; } = ElementId.InvalidElementId;
            public string Label { get; set; } = string.Empty;
            public XYZ Point { get; set; } = XYZ.Zero;
        }

        private class CandidateEgressPath
        {
            public PathOfTravel Path { get; set; } = null!;
            public int StartIndex { get; set; }
            public int ExitIndex { get; set; }
            public double LengthFeet { get; set; }
        }

        private static readonly IReadOnlyList<EgressSharedParamSpec> RoomSharedParamSpecs = new List<EgressSharedParamSpec>
        {
            new EgressSharedParamSpec
            {
                Name = ParamActualDist,
                Guid = GuidActualDist,
                DataType = SpecTypeId.Length,
                GroupType = GroupTypeId.Geometry
            },
            new EgressSharedParamSpec
            {
                Name = ParamMaxAllowed,
                Guid = GuidMaxAllowed,
                DataType = SpecTypeId.Length,
                GroupType = GroupTypeId.Geometry
            },
            new EgressSharedParamSpec
            {
                Name = ParamIsCompliant,
                Guid = GuidIsCompliant,
                DataType = SpecTypeId.String.Text,
                GroupType = GroupTypeId.Data
            },
            new EgressSharedParamSpec
            {
                Name = ParamTargetExit,
                Guid = GuidTargetExit,
                DataType = SpecTypeId.String.Text,
                GroupType = GroupTypeId.IdentityData
            }
        };

        /// <summary>
        /// 自動檢查並在房間（OST_Rooms）品類建立 4 個避難檢核共用參數。
        /// </summary>
        public static void EnsureRoomSharedParameters(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            // 先檢查是否所有參數皆已存在
            bool allExist = true;
            foreach (var spec in RoomSharedParamSpecs)
            {
                if (SharedParameterElement.Lookup(doc, spec.Guid) == null)
                {
                    allExist = false;
                    break;
                }
            }

            if (allExist)
                return;

            var app = doc.Application;
            string originalSharedParametersFilename = app.SharedParametersFilename;
            string tempFilePath = Path.Combine(
                Path.GetTempPath(),
                $"RevitMCP_EgressParams_{Guid.NewGuid():N}.txt");

            try
            {
                File.WriteAllText(tempFilePath, string.Empty);
                app.SharedParametersFilename = tempFilePath;

                DefinitionFile defFile = app.OpenSharedParameterFile();
                if (defFile == null)
                    throw new InvalidOperationException($"無法開啟臨時共用參數檔: {tempFilePath}");

                DefinitionGroup defGroup = defFile.Groups.get_Item("EgressAudit")
                    ?? defFile.Groups.Create("EgressAudit");

                Category roomCategory = Category.GetCategory(doc, BuiltInCategory.OST_Rooms);
                if (roomCategory == null)
                    throw new InvalidOperationException("找不到房間品類 (OST_Rooms)");

                CategorySet categorySet = app.Create.NewCategorySet();
                categorySet.Insert(roomCategory);

                bool ownTransaction = !doc.IsModifiable;
                Transaction? trans = ownTransaction ? new Transaction(doc, "建立避難檢核房間共用參數") : null;

                try
                {
                    trans?.Start();

                    foreach (var spec in RoomSharedParamSpecs)
                    {
                        // 1. 先用 SharedParameterElement.Lookup(doc, guid) 檢查是否已存在，若已存在則跳過
                        SharedParameterElement existingElement = SharedParameterElement.Lookup(doc, spec.Guid);
                        if (existingElement != null)
                            continue;

                        // 2. 建立外部參數定義（指定固定 GUID 與型別）
                        var options = new ExternalDefinitionCreationOptions(spec.Name, spec.DataType)
                        {
                            GUID = spec.Guid,
                            Visible = true,
                            UserModifiable = true
                        };

                        Definition externalDef = defGroup.Definitions.Create(options);

                        // 3. 綁定為 InstanceBinding 至 OST_Rooms
                        InstanceBinding instanceBinding = app.Create.NewInstanceBinding(categorySet);
                        bool bound = doc.ParameterBindings.Insert(externalDef, instanceBinding, spec.GroupType);
                        if (!bound)
                        {
                            doc.ParameterBindings.ReInsert(externalDef, instanceBinding, spec.GroupType);
                        }

                        // 4. 透過 InternalDefinition.SetAllowVaryBetweenGroups(doc, true) 開啟「值可以依群組實例而異」
                        //    注意：Revit API 中 SpecTypeId.Length 等長度型別不支援 AllowVaryBetweenGroups，需捕獲 ArgumentException
                        SharedParameterElement createdElement = SharedParameterElement.Lookup(doc, spec.Guid);
                        InternalDefinition? internalDef = createdElement?.GetDefinition();
                        if (internalDef != null)
                        {
                            try
                            {
                                internalDef.SetAllowVaryBetweenGroups(doc, true);
                            }
                            catch (Autodesk.Revit.Exceptions.ArgumentException)
                            {
                                // SpecTypeId.Length 不支援 VaryBetweenGroups，略過即可
                            }
                        }
                    }

                    trans?.Commit();
                }
                catch
                {
                    if (trans != null && trans.HasStarted() && !trans.HasEnded())
                    {
                        trans.RollBack();
                    }
                    throw;
                }
                finally
                {
                    trans?.Dispose();
                }
            }
            finally
            {
                // 確保無論成功或失敗都會還原 app.SharedParametersFilename 並清理暫存檔
                try
                {
                    app.SharedParametersFilename = originalSharedParametersFilename ?? string.Empty;
                }
                catch
                {
                    // 忽略原路徑無效時的還原例外
                }

                try
                {
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch
                {
                    // 忽略暫存檔刪除失敗
                }
            }
        }

        /// <summary>
        /// 從 JSON 參數解析並執行避難路徑檢核（供 CommandExecutor 呼叫）
        /// </summary>
        public static object ExecuteEgressAudit(Document doc, JObject parameters)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            List<long> roomIds = parameters["roomIds"]?.Values<long>().ToList() ?? new List<long>();
            List<long> exitDoorIds = parameters["exitDoorIds"]?.Values<long>().ToList() ?? new List<long>();
            double maxAllowedMeters = parameters["maxAllowedMeters"]?.Value<double>() ?? 30.0;
            bool dimensionSegments = parameters["dimensionSegments"]?.Value<bool>() ?? true;

            return ExecuteEgressAudit(doc, roomIds, exitDoorIds, maxAllowedMeters, dimensionSegments);
        }

        /// <summary>
        /// 執行房間避難路徑檢核 (audit_room_egress)
        /// </summary>
        public static object ExecuteEgressAudit(
            Document doc,
            IList<long> roomIds,
            IList<long> exitDoorIds,
            double maxAllowedMeters,
            bool dimensionSegments = true)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            if (roomIds == null || roomIds.Count == 0)
                throw new ArgumentException("請提供至少一個房間 ID (roomIds)");
            if (exitDoorIds == null || exitDoorIds.Count == 0)
                throw new ArgumentException("請提供至少一個出口門 ID (exitDoorIds)");
            if (maxAllowedMeters <= 0)
                throw new ArgumentException("法規容許上限 (maxAllowedMeters) 必須大於 0");

            double maxAllowedFeet = UnitUtils.ConvertToInternalUnits(maxAllowedMeters, UnitTypeId.Meters);
            double insetFeet = UnitUtils.ConvertToInternalUnits(0.3, UnitTypeId.Meters);
            double minSegmentFeet = UnitUtils.ConvertToInternalUnits(0.1, UnitTypeId.Meters); // 最小標註線段門檻 10cm (0.1m)

            // 解析所有出口門元素與座標
            var exitTargets = new List<ExitDoorTarget>();
            foreach (long rawDoorId in exitDoorIds)
            {
                ElementId doorElemId = ((IdType)rawDoorId).ToElementId();
                Element doorElem = doc.GetElement(doorElemId);
                if (doorElem == null)
                    continue;

                XYZ? doorPt = GetElementCenterPoint(doorElem);
                if (doorPt == null)
                    continue;

                string mark = doorElem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty;
                string label = !string.IsNullOrWhiteSpace(mark)
                    ? $"{mark} (ID:{rawDoorId})"
                    : $"{doorElem.Name} (ID:{rawDoorId})";

                exitTargets.Add(new ExitDoorTarget
                {
                    DoorId = rawDoorId,
                    ElementId = doorElemId,
                    Label = label,
                    Point = doorPt
                });
            }

            if (exitTargets.Count == 0)
                throw new InvalidOperationException("無法從 exitDoorIds 解析出任何有效的出口門座標");

            ElementId dashPatternId = FindDashLinePatternId(doc);
            var roomResults = new List<object>();
            int compliantCount = 0;
            int nonCompliantCount = 0;
            int failedCount = 0;

            using (TransactionGroup tg = new TransactionGroup(doc, "房間避難路徑檢核"))
            {
                tg.Start();

                // 1. 先確保 OST_Rooms 的 4 個共用參數已建立並綁定
                EnsureRoomSharedParameters(doc);

                // 2. 逐一檢核每個 Room
                foreach (long rawRoomId in roomIds)
                {
                    ElementId roomElemId = ((IdType)rawRoomId).ToElementId();
                    Room? room = doc.GetElement(roomElemId) as Room;

                    if (room == null || room.Area <= 0)
                    {
                        failedCount++;
                        roomResults.Add(new
                        {
                            RoomId = rawRoomId,
                            Success = false,
                            Error = room == null ? "找不到該房間元素" : "房間未封閉或面積為 0"
                        });
                        continue;
                    }

                    ViewPlan? viewPlan = ResolveFloorPlanForRoom(doc, room);
                    if (viewPlan == null)
                    {
                        failedCount++;
                        roomResults.Add(new
                        {
                            RoomId = rawRoomId,
                            RoomName = room.Name,
                            Success = false,
                            Error = "找不到對應樓層的平面視圖 (ViewPlan)"
                        });
                        continue;
                    }

                    double planElevation = viewPlan.GenLevel != null
                        ? viewPlan.GenLevel.Elevation
                        : (doc.GetElement(room.LevelId) as Level)?.Elevation ?? 0.0;

                    List<XYZ> startPoints = GetInsetRoomBoundaryVertices(room, insetFeet, planElevation);
                    if (startPoints.Count == 0)
                    {
                        failedCount++;
                        roomResults.Add(new
                        {
                            RoomId = rawRoomId,
                            RoomName = room.Name,
                            Success = false,
                            Error = "無法取得房間外圈頂點或內縮點"
                        });
                        continue;
                    }

                    List<XYZ> exitPointsOnPlan = exitTargets
                        .Select(t => new XYZ(t.Point.X, t.Point.Y, planElevation))
                        .ToList();

                    using (Transaction trans = new Transaction(doc, $"避難路徑檢核 - {room.Name}"))
                    {
                        trans.Start();

                        IList<PathOfTravel> createdPaths = PathOfTravel.CreateMapped(viewPlan, startPoints, exitPointsOnPlan);

                        var validCandidates = new List<CandidateEgressPath>();
                        if (createdPaths != null)
                        {
                            foreach (PathOfTravel p in createdPaths)
                            {
                                if (p == null || !p.IsValidObject)
                                    continue;

                                double lengthFeet = GetPathOfTravelLengthFeet(p);
                                if (lengthFeet <= 1e-6)
                                    continue;

                                int startIdx = FindClosestPointIndex(p.PathStart, startPoints);
                                int exitIdx = FindClosestPointIndex(p.PathEnd, exitPointsOnPlan);

                                validCandidates.Add(new CandidateEgressPath
                                {
                                    Path = p,
                                    StartIndex = startIdx,
                                    ExitIndex = exitIdx,
                                    LengthFeet = lengthFeet
                                });
                            }
                        }

                        // 演算法篩選：
                        // (1) 每個房間頂點 (StartIndex) 先找離它「最近」的出口路徑（代表該頂點的最佳逃生方向）
                        // (2) 再從所有頂點的最佳逃生路徑中，保留「長度最長」的那一條（代表該房間最不利逃生距離）
                        CandidateEgressPath? worstBestPath = validCandidates
                            .GroupBy(c => c.StartIndex)
                            .Select(g => g.OrderBy(c => c.LengthFeet).First())
                            .OrderByDescending(c => c.LengthFeet)
                            .FirstOrDefault();

                        // 其餘路徑全數 doc.Delete()
                        if (createdPaths != null)
                        {
                            ElementId keepId = worstBestPath?.Path.Id ?? ElementId.InvalidElementId;
                            foreach (PathOfTravel p in createdPaths)
                            {
                                if (p != null && p.IsValidObject && p.Id != keepId)
                                {
                                    doc.Delete(p.Id);
                                }
                            }
                        }

                        if (worstBestPath == null)
                        {
                            trans.Commit();
                            failedCount++;
                            roomResults.Add(new
                            {
                                RoomId = rawRoomId,
                                RoomName = room.Name,
                                RoomNumber = room.Number,
                                Success = false,
                                Error = "PathOfTravel.CreateMapped 未能計算出任何有效路徑"
                            });
                            continue;
                        }

                        double actualDistFeet = worstBestPath.LengthFeet;
                        double actualDistMeters = Math.Round(
                            UnitUtils.ConvertFromInternalUnits(actualDistFeet, UnitTypeId.Meters), 2);
                        bool isCompliant = actualDistFeet <= maxAllowedFeet + 1e-6;
                        string complianceStatus = isCompliant ? "合格" : "不合格";
                        ExitDoorTarget chosenExit = exitTargets[worstBestPath.ExitIndex];

                        // 4. 寫入房間的 4 個 GUID 共用參數（長度以 Internal Feet 寫入）
                        SetRoomParameter(room, GuidActualDist, actualDistFeet);
                        SetRoomParameter(room, GuidMaxAllowed, maxAllowedFeet);
                        SetRoomParameter(room, GuidIsCompliant, complianceStatus);
                        SetRoomParameter(room, GuidTargetExit, chosenExit.Label);

                        // 5. 使用 View.SetElementOverrides 上色並嘗試建立 IndependentTag
                        OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                        if (isCompliant)
                        {
                            ogs.SetProjectionLineColor(new Color(0, 170, 60));
                            ogs.SetProjectionLineWeight(5);
                        }
                        else
                        {
                            ogs.SetProjectionLineColor(new Color(220, 20, 20));
                            ogs.SetProjectionLineWeight(7);
                            if (dashPatternId != ElementId.InvalidElementId)
                            {
                                ogs.SetProjectionLinePatternId(dashPatternId);
                            }
                        }
                        viewPlan.SetElementOverrides(worstBestPath.Path.Id, ogs);

                        ElementId? createdTagId = TryCreatePathMidpointTag(doc, viewPlan, worstBestPath.Path);

                        // 6. 針對逃生路徑每個轉折線段（>= 10cm）建立 Revit 原生線性尺寸標註
                        var (segmentLengthsMeters, segmentDimensionIds) = CreatePathSegmentDimensions(
                            doc,
                            viewPlan,
                            worstBestPath.Path,
                            planElevation,
                            minSegmentFeet,
                            dimensionSegments,
                            isCompliant);

                        trans.Commit();

                        if (isCompliant)
                            compliantCount++;
                        else
                            nonCompliantCount++;

                        roomResults.Add(new
                        {
                            RoomId = rawRoomId,
                            RoomName = room.Name,
                            RoomNumber = room.Number,
                            Success = true,
                            PathOfTravelId = worstBestPath.Path.Id.GetIdValue(),
                            TagId = createdTagId?.GetIdValue(),
                            ActualDistanceMeters = actualDistMeters,
                            MaxAllowedMeters = maxAllowedMeters,
                            SegmentLengthsMeters = segmentLengthsMeters,
                            SegmentDimensionIds = segmentDimensionIds,
                            IsCompliant = complianceStatus,
                            TargetExit = chosenExit.Label,
                            TargetExitDoorId = chosenExit.DoorId
                        });
                    }
                }

                tg.Assimilate();
            }

            return new
            {
                TotalRooms = roomIds.Count,
                CompliantCount = compliantCount,
                NonCompliantCount = nonCompliantCount,
                FailedCount = failedCount,
                MaxAllowedMeters = maxAllowedMeters,
                Results = roomResults
            };
        }

        /// <summary>
        /// 取得房間外圈邊界頂點，並向房間中心（或 8 方位放射搭配 room.IsPointInRoom）內縮指定距離。
        /// </summary>
        private static List<XYZ> GetInsetRoomBoundaryVertices(Room room, double insetFeet, double planElevation)
        {
            var result = new List<XYZ>();
            var boundaryOptions = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            };

            IList<IList<BoundarySegment>>? loops = room.GetBoundarySegments(boundaryOptions);
            if (loops == null || loops.Count == 0)
            {
                if (room.Location is LocationPoint lp && room.IsPointInRoom(lp.Point))
                {
                    result.Add(new XYZ(lp.Point.X, lp.Point.Y, planElevation));
                }
                return result;
            }

            // 取周長最長的迴圈作為房間外圈邊界
            IList<BoundarySegment> outerLoop = loops
                .OrderByDescending(loop => loop.Sum(seg => seg.GetCurve()?.Length ?? 0.0))
                .First();

            var rawVertices = new List<XYZ>();
            foreach (BoundarySegment seg in outerLoop)
            {
                Curve? curve = seg.GetCurve();
                if (curve == null)
                    continue;

                XYZ pt = curve.GetEndPoint(0);
                if (rawVertices.Count == 0 || rawVertices.Last().DistanceTo(pt) > 1e-3)
                {
                    rawVertices.Add(pt);
                }
            }

            if (rawVertices.Count == 0)
                return result;

            // 計算房間中心點（以頂點幾何中心為主，若需要也可參考 LocationPoint）
            double avgX = rawVertices.Average(v => v.X);
            double avgY = rawVertices.Average(v => v.Y);
            double testZ = rawVertices[0].Z;
            XYZ roomCenter = new XYZ(avgX, avgY, testZ);

            foreach (XYZ vertex in rawVertices)
            {
                XYZ? validInsetPoint = null;

                // 策略 A：直接往房間中心方向內縮 0.3m
                XYZ toCenter = new XYZ(roomCenter.X - vertex.X, roomCenter.Y - vertex.Y, 0);
                if (toCenter.GetLength() > 1e-6)
                {
                    XYZ dirToCenter = toCenter.Normalize();
                    XYZ candidate = new XYZ(
                        vertex.X + dirToCenter.X * insetFeet,
                        vertex.Y + dirToCenter.Y * insetFeet,
                        testZ);

                    if (room.IsPointInRoom(candidate))
                    {
                        validInsetPoint = new XYZ(candidate.X, candidate.Y, planElevation);
                    }
                }

                // 策略 B：若往中心內縮點不在房間內（例如 L 型或凹多邊形房間），改用 8 方位放射搭配 room.IsPointInRoom
                if (validInsetPoint == null)
                {
                    XYZ preferredDir = toCenter.GetLength() > 1e-6 ? toCenter.Normalize() : XYZ.BasisX;
                    foreach (XYZ dir in EightDirections.OrderByDescending(d => d.DotProduct(preferredDir)))
                    {
                        XYZ candidate = new XYZ(
                            vertex.X + dir.X * insetFeet,
                            vertex.Y + dir.Y * insetFeet,
                            testZ);

                        if (room.IsPointInRoom(candidate))
                        {
                            validInsetPoint = new XYZ(candidate.X, candidate.Y, planElevation);
                            break;
                        }
                    }
                }

                if (validInsetPoint != null &&
                    !result.Any(existing => existing.DistanceTo(validInsetPoint) < 1e-2))
                {
                    result.Add(validInsetPoint);
                }
            }

            // 保底：若所有頂點內縮後皆不在房間內，使用房間 LocationPoint
            if (result.Count == 0 && room.Location is LocationPoint locPt)
            {
                result.Add(new XYZ(locPt.Point.X, locPt.Point.Y, planElevation));
            }

            return result;
        }

        private static ViewPlan? ResolveFloorPlanForRoom(Document doc, Room room)
        {
            if (doc.ActiveView is ViewPlan activePlan &&
                !activePlan.IsTemplate &&
                activePlan.ViewType == ViewType.FloorPlan &&
                activePlan.GenLevel != null &&
                activePlan.GenLevel.Id == room.LevelId)
            {
                return activePlan;
            }

            ViewPlan? matchingPlan = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(vp =>
                    !vp.IsTemplate &&
                    vp.ViewType == ViewType.FloorPlan &&
                    vp.GenLevel != null &&
                    vp.GenLevel.Id == room.LevelId);

            return matchingPlan ?? (doc.ActiveView as ViewPlan);
        }

        private static XYZ? GetElementCenterPoint(Element elem)
        {
            if (elem.Location is LocationPoint lp)
                return lp.Point;

            if (elem.Location is LocationCurve lc && lc.Curve != null)
                return lc.Curve.Evaluate(0.5, true);

            BoundingBoxXYZ? bbox = elem.get_BoundingBox(null);
            if (bbox != null)
                return (bbox.Min + bbox.Max) * 0.5;

            return null;
        }

        private static double GetPathOfTravelLengthFeet(PathOfTravel path)
        {
            IList<Curve>? curves = path.GetCurves();
            if (curves != null && curves.Count > 0)
            {
                double curveLengthSum = curves.Sum(c => c?.Length ?? 0.0);
                if (curveLengthSum > 1e-6)
                    return curveLengthSum;
            }

            Parameter? lengthParam = path.LookupParameter("Length") ?? path.LookupParameter("長度");
            if (lengthParam != null && lengthParam.HasValue && lengthParam.StorageType == StorageType.Double)
            {
                return lengthParam.AsDouble();
            }

            return 0.0;
        }

        private static int FindClosestPointIndex(XYZ target, IList<XYZ> points)
        {
            int bestIndex = 0;
            double minDist = double.MaxValue;

            for (int i = 0; i < points.Count; i++)
            {
                double d = Math.Sqrt(
                    Math.Pow(target.X - points[i].X, 2) +
                    Math.Pow(target.Y - points[i].Y, 2));
                if (d < minDist)
                {
                    minDist = d;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static void SetRoomParameter(Room room, Guid paramGuid, double value)
        {
            Parameter? param = room.get_Parameter(paramGuid);
            if (param != null && !param.IsReadOnly)
            {
                param.Set(value);
            }
        }

        private static void SetRoomParameter(Room room, Guid paramGuid, string value)
        {
            Parameter? param = room.get_Parameter(paramGuid);
            if (param != null && !param.IsReadOnly)
            {
                param.Set(value ?? string.Empty);
            }
        }

        private static ElementId FindDashLinePatternId(Document doc)
        {
            LinePatternElement? dashPattern = new FilteredElementCollector(doc)
                .OfClass(typeof(LinePatternElement))
                .Cast<LinePatternElement>()
                .FirstOrDefault(lp =>
                    lp.Name.Equals("Dash", StringComparison.OrdinalIgnoreCase) ||
                    lp.Name.IndexOf("Dash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    lp.Name.Contains("虛線"));

            return dashPattern?.Id ?? ElementId.InvalidElementId;
        }

        private static ElementId? TryCreatePathMidpointTag(Document doc, ViewPlan viewPlan, PathOfTravel path)
        {
            try
            {
                XYZ midPoint = GetPathMidpoint(path);
                IndependentTag? tag = IndependentTag.Create(
                    doc,
                    viewPlan.Id,
                    new Reference(path),
                    false,
                    TagMode.TM_ADDBY_CATEGORY,
                    TagOrientation.Horizontal,
                    midPoint);

                return tag?.Id;
            }
            catch
            {
                // 若專案尚未載入移動路徑標籤族群，忽略標籤建立失敗，不中斷整體檢核流程
                return null;
            }
        }

        private static XYZ GetPathMidpoint(PathOfTravel path)
        {
            IList<Curve>? curves = path.GetCurves();
            if (curves == null || curves.Count == 0)
            {
                return (path.PathStart + path.PathEnd) * 0.5;
            }

            double totalLength = curves.Sum(c => c.Length);
            double halfLength = totalLength * 0.5;
            double accumulated = 0.0;

            foreach (Curve curve in curves)
            {
                if (accumulated + curve.Length >= halfLength && curve.Length > 1e-6)
                {
                    double normalizedParam = (halfLength - accumulated) / curve.Length;
                    normalizedParam = Math.Max(0.0, Math.Min(1.0, normalizedParam));
                    return curve.Evaluate(normalizedParam, true);
                }
                accumulated += curve.Length;
            }

            return curves[curves.Count / 2].Evaluate(0.5, true);
        }

        /// <summary>
        /// 針對逃生路徑的每個轉折線段提取長度，並對長度 >= minSegmentFeet (預設 10cm) 的線段建立 Revit 原生線性尺寸標註 (Dimension)。
        /// </summary>
        private static (List<double> SegmentLengthsMeters, List<long> SegmentDimensionIds) CreatePathSegmentDimensions(
            Document doc,
            ViewPlan viewPlan,
            PathOfTravel path,
            double planElevation,
            double minSegmentFeet,
            bool dimensionSegments,
            bool isCompliant)
        {
            var segmentLengthsMeters = new List<double>();
            var segmentDimensionIds = new List<long>();

            IList<Curve>? curves = path.GetCurves();
            if (curves == null || curves.Count == 0)
            {
                return (segmentLengthsMeters, segmentDimensionIds);
            }

            GraphicsStyle? invisibleStyle = dimensionSegments ? TryFindInvisibleLineStyle(doc) : null;
            OverrideGraphicSettings dimOgs = new OverrideGraphicSettings();
            dimOgs.SetProjectionLineColor(isCompliant ? new Color(0, 170, 60) : new Color(220, 20, 20));

            double tickHalfLen = 0.25; // 垂直隱形參考短線半長（約 75mm）

            foreach (Curve curve in curves)
            {
                if (curve == null || curve.Length <= 1e-6)
                    continue;

                double segMeters = Math.Round(
                    UnitUtils.ConvertFromInternalUnits(curve.Length, UnitTypeId.Meters), 2);
                segmentLengthsMeters.Add(segMeters);

                // 過濾小於 10cm (minSegmentFeet) 的微小轉折碎線段，或未啟用分段標註時跳過
                if (!dimensionSegments || curve.Length < minSegmentFeet)
                    continue;

                try
                {
                    XYZ p0 = curve.GetEndPoint(0);
                    XYZ p1 = curve.GetEndPoint(1);
                    XYZ start = new XYZ(p0.X, p0.Y, planElevation);
                    XYZ end = new XYZ(p1.X, p1.Y, planElevation);

                    double planarLength = start.DistanceTo(end);
                    if (planarLength < minSegmentFeet)
                        continue;

                    XYZ dir = (end - start).Normalize();
                    XYZ perp = new XYZ(-dir.Y, dir.X, 0);
                    if (perp.GetLength() <= 1e-6)
                    {
                        perp = XYZ.BasisY;
                    }
                    else
                    {
                        perp = perp.Normalize();
                    }

                    // 於轉折線段兩端建立垂直於線段方向的微短參考 DetailCurve（套用 <Invisible lines> 隱藏線樣式）
                    DetailCurve dc1 = doc.Create.NewDetailCurve(
                        viewPlan,
                        Line.CreateBound(start - perp * tickHalfLen, start + perp * tickHalfLen));
                    DetailCurve dc2 = doc.Create.NewDetailCurve(
                        viewPlan,
                        Line.CreateBound(end - perp * tickHalfLen, end + perp * tickHalfLen));

                    if (invisibleStyle != null)
                    {
                        try
                        {
                            dc1.LineStyle = invisibleStyle;
                            dc2.LineStyle = invisibleStyle;
                        }
                        catch
                        {
                            // 若線樣式套用失敗則忽略
                        }
                    }

                    ReferenceArray refArray = new ReferenceArray();
                    refArray.Append(dc1.GeometryCurve.Reference);
                    refArray.Append(dc2.GeometryCurve.Reference);

                    // 尺寸線直接建立於逃生路徑轉折線段上
                    Line dimLine = Line.CreateBound(start, end);
                    Dimension? dim = doc.Create.NewDimension(viewPlan, dimLine, refArray);
                    if (dim != null)
                    {
                        viewPlan.SetElementOverrides(dim.Id, dimOgs);
                        segmentDimensionIds.Add(dim.Id.GetIdValue());
                    }
                }
                catch
                {
                    // 單一線段標註失敗不中斷整體檢核與其餘線段標註
                }
            }

            return (segmentLengthsMeters, segmentDimensionIds);
        }

        private static GraphicsStyle? TryFindInvisibleLineStyle(Document doc)
        {
            try
            {
                Category? invisibleCat = Category.GetCategory(doc, BuiltInCategory.OST_InvisibleLines);
                GraphicsStyle? style = invisibleCat?.GetGraphicsStyle(GraphicsStyleType.Projection);
                if (style != null)
                    return style;

                Category? linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                if (linesCat != null)
                {
                    foreach (Category subCat in linesCat.SubCategories)
                    {
                        string name = subCat.Name ?? string.Empty;
                        if (name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("不可見", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("隱藏", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return subCat.GetGraphicsStyle(GraphicsStyleType.Projection);
                        }
                    }
                }
            }
            catch
            {
                // 忽略查詢例外
            }

            return null;
        }
    }
}
