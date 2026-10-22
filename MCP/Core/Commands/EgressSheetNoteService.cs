using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core
{
    /// <summary>
    /// 避難檢核圖紙自動排版服務 (Egress Sheet &amp; Note Service)
    /// </summary>
    public class EgressSheetNoteService
    {
        public const string DefaultScheduleName = "避難步行距離檢討表";

        /// <summary>
        /// 從 JSON 參數解析並執行避難檢核圖紙自動排版（供 CommandExecutor 呼叫）
        /// </summary>
        public static object GenerateEgressSheetNote(Document doc, JObject parameters)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            string sheetNumber = parameters["sheetNumber"]?.Value<string>()?.Trim() ?? string.Empty;
            string regulationNoteText = parameters["regulationNoteText"]?.Value<string>() ?? string.Empty;

            return GenerateEgressSheetNote(doc, sheetNumber, regulationNoteText);
        }

        /// <summary>
        /// 在指定圖紙上自動建立並配置「避難步行距離檢討表」與法規檢討綜整說明文字 (generate_egress_sheet_note)
        /// </summary>
        public static object GenerateEgressSheetNote(
            Document doc,
            string sheetNumber,
            string regulationNoteText)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(sheetNumber))
                throw new ArgumentException("請提供目標圖紙圖號 (sheetNumber)", nameof(sheetNumber));

            if (string.IsNullOrWhiteSpace(regulationNoteText))
            {
                regulationNoteText = "【避難步行距離法規檢討說明】\n" +
                                     "1. 依據建築技術規則建築設計施工編第 93 條規定檢討各居室至直通樓梯／安全梯之步行距離。\n" +
                                     "2. 檢討方式採各居室最遠角落頂點內縮 0.3m 起算，取各頂點至最近出口之最不利逃生路徑長度。";
            }

            // 預先換算合理的文字寬度（150mm 轉為英尺）以便自動換行
            double textWidthFeet = UnitUtils.ConvertToInternalUnits(150.0, UnitTypeId.Millimeters);

            using (TransactionGroup tg = new TransactionGroup(doc, "避難檢核圖紙自動排版"))
            {
                tg.Start();

                try
                {
                    // 尋找目標圖紙：透過 FilteredElementCollector 尋找 ViewSheet，比對 SheetNumber == sheetNumber
                    ViewSheet? sheet = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .FirstOrDefault(s =>
                            !s.IsTemplate &&
                            string.Equals(s.SheetNumber?.Trim(), sheetNumber.Trim(), StringComparison.OrdinalIgnoreCase));

                    if (sheet == null)
                    {
                        throw new InvalidOperationException($"找不到圖號為 '{sheetNumber}' 的圖紙 (ViewSheet)");
                    }

                    // 確保房間的 4 個避難檢核共用參數已綁定至 OST_Rooms
                    EgressAuditService.EnsureRoomSharedParameters(doc);

                    ViewSchedule schedule;
                    ScheduleSheetInstance scheduleInstance;
                    TextNote textNote;
                    XYZ schedulePoint;
                    XYZ textLocationXyz;
                    var addedFieldNames = new List<string>();

                    using (Transaction trans = new Transaction(doc, "建立避難步行距離檢討表與圖紙說明"))
                    {
                        trans.Start();

                        // 若已有同名的舊明細表，先移除舊明細表以確保名稱可設為「避難步行距離檢討表」
                        ViewSchedule? existingSchedule = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewSchedule))
                            .Cast<ViewSchedule>()
                            .FirstOrDefault(vs =>
                                !vs.IsTemplate &&
                                string.Equals(vs.Name, DefaultScheduleName, StringComparison.OrdinalIgnoreCase));

                        if (existingSchedule != null)
                        {
                            try
                            {
                                doc.Delete(existingSchedule.Id);
                            }
                            catch
                            {
                                // 若舊明細表因故無法刪除，後續命名會自動附加序號避免衝突
                            }
                        }

                        // 4. 建立房間法規檢討明細表
                        schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Rooms));
                        SetScheduleNameSafely(doc, schedule, DefaultScheduleName);

                        // 加入欄位（ScheduleField）：
                        // 包含「房間編號(Number)」、「房間名稱(Name)」，以及 4 個共用參數 GUID
                        // (Egress_Actual_Dist, Egress_Max_Allowed, Egress_Target_Exit, Egress_Is_Compliant)
                        IList<SchedulableField> schedulableFields = schedule.Definition.GetSchedulableFields();

                        AddBuiltInField(doc, schedule, schedulableFields, BuiltInParameter.ROOM_NUMBER, new[] { "Number", "編號" }, addedFieldNames);
                        AddBuiltInField(doc, schedule, schedulableFields, BuiltInParameter.ROOM_NAME, new[] { "Name", "名稱" }, addedFieldNames);

                        AddSharedParameterField(doc, schedule, schedulableFields, EgressAuditService.GuidActualDist, EgressAuditService.ParamActualDist, addedFieldNames);
                        AddSharedParameterField(doc, schedule, schedulableFields, EgressAuditService.GuidMaxAllowed, EgressAuditService.ParamMaxAllowed, addedFieldNames);
                        AddSharedParameterField(doc, schedule, schedulableFields, EgressAuditService.GuidTargetExit, EgressAuditService.ParamTargetExit, addedFieldNames);
                        AddSharedParameterField(doc, schedule, schedulableFields, EgressAuditService.GuidIsCompliant, EgressAuditService.ParamIsCompliant, addedFieldNames);

                        // 5. 放置明細表至圖紙：計算圖紙右下角適當座標（XYZ）
                        schedulePoint = ComputeSheetBottomRightSchedulePoint(doc, sheet);
                        scheduleInstance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, schedulePoint);

                        // 6. 建立法規檢討說明文字（放置在明細表下方或上方）
                        textLocationXyz = ComputeTextNoteLocation(sheet, scheduleInstance, schedulePoint);

                        ElementId textTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                        if (textTypeId == ElementId.InvalidElementId)
                        {
                            textTypeId = new FilteredElementCollector(doc)
                                .OfClass(typeof(TextNoteType))
                                .FirstElementId();
                        }

                        if (textTypeId == ElementId.InvalidElementId)
                        {
                            throw new InvalidOperationException("專案中找不到可用的文字類型 (TextNoteType)");
                        }

                        TextNoteOptions textNoteOptions = new TextNoteOptions(textTypeId)
                        {
                            HorizontalAlignment = HorizontalTextAlignment.Left
                        };

                        textNote = TextNote.Create(doc, sheet.Id, textLocationXyz, regulationNoteText, textNoteOptions);
                        if (textNote != null)
                        {
                            textNote.Width = textWidthFeet;
                        }

                        trans.Commit();
                    }

                    tg.Assimilate();

                    return new
                    {
                        Success = true,
                        SheetId = sheet.Id.GetIdValue(),
                        SheetNumber = sheet.SheetNumber,
                        SheetName = sheet.Name,
                        ScheduleId = schedule.Id.GetIdValue(),
                        ScheduleName = schedule.Name,
                        ScheduleSheetInstanceId = scheduleInstance.Id.GetIdValue(),
                        AddedFields = addedFieldNames,
                        TextNoteId = textNote?.Id.GetIdValue(),
                        TextWidthMm = 150.0,
                        ScheduleLocationMm = new
                        {
                            X = Math.Round(UnitUtils.ConvertFromInternalUnits(schedulePoint.X, UnitTypeId.Millimeters), 2),
                            Y = Math.Round(UnitUtils.ConvertFromInternalUnits(schedulePoint.Y, UnitTypeId.Millimeters), 2)
                        },
                        TextNoteLocationMm = new
                        {
                            X = Math.Round(UnitUtils.ConvertFromInternalUnits(textLocationXyz.X, UnitTypeId.Millimeters), 2),
                            Y = Math.Round(UnitUtils.ConvertFromInternalUnits(textLocationXyz.Y, UnitTypeId.Millimeters), 2)
                        },
                        Message = $"已於圖紙 {sheet.SheetNumber} ({sheet.Name}) 建立「{schedule.Name}」與法規檢討說明文字"
                    };
                }
                catch
                {
                    if (tg.HasStarted())
                    {
                        tg.RollBack();
                    }
                    throw;
                }
            }
        }

        private static void SetScheduleNameSafely(Document doc, ViewSchedule schedule, string baseName)
        {
            var existingNames = new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.Id != schedule.Id)
                    .Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            if (!existingNames.Contains(baseName))
            {
                schedule.Name = baseName;
                return;
            }

            int suffix = 2;
            while (existingNames.Contains($"{baseName} ({suffix})"))
            {
                suffix++;
            }
            schedule.Name = $"{baseName} ({suffix})";
        }

        private static void AddBuiltInField(
            Document doc,
            ViewSchedule schedule,
            IList<SchedulableField> schedulableFields,
            BuiltInParameter bip,
            string[] fallbackNames,
            List<string> addedFieldNames)
        {
            ElementId targetParamId = new ElementId(bip);
            SchedulableField? matchedField = schedulableFields
                .FirstOrDefault(sf => sf.ParameterId == targetParamId);

            if (matchedField == null && fallbackNames != null && fallbackNames.Length > 0)
            {
                matchedField = schedulableFields.FirstOrDefault(sf =>
                {
                    string fieldName = sf.GetName(doc) ?? string.Empty;
                    return fallbackNames.Any(n => string.Equals(fieldName, n, StringComparison.OrdinalIgnoreCase));
                });
            }

            if (matchedField != null)
            {
                ScheduleField field = schedule.Definition.AddField(matchedField);
                addedFieldNames.Add(field.GetName());
            }
        }

        private static void AddSharedParameterField(
            Document doc,
            ViewSchedule schedule,
            IList<SchedulableField> schedulableFields,
            Guid paramGuid,
            string paramName,
            List<string> addedFieldNames)
        {
            SharedParameterElement? sharedParamElem = SharedParameterElement.Lookup(doc, paramGuid);
            SchedulableField? matchedField = null;

            if (sharedParamElem != null)
            {
                matchedField = schedulableFields
                    .FirstOrDefault(sf => sf.ParameterId == sharedParamElem.Id);
            }

            if (matchedField == null)
            {
                matchedField = schedulableFields.FirstOrDefault(sf =>
                    string.Equals(sf.GetName(doc), paramName, StringComparison.OrdinalIgnoreCase));
            }

            if (matchedField == null)
            {
                throw new InvalidOperationException($"在房間明細表可排程欄位中找不到共用參數: {paramName} ({paramGuid})");
            }

            ScheduleField field = schedule.Definition.AddField(matchedField);
            addedFieldNames.Add(field.GetName());
        }

        /// <summary>
        /// 計算圖紙右下角適合放置明細表的座標（XYZ）。
        /// 優先讀取圖紙上的圖框 (OST_TitleBlocks) 邊界或 Sheet.Outline，於右下角向內留設合理邊距。
        /// </summary>
        private static XYZ ComputeSheetBottomRightSchedulePoint(Document doc, ViewSheet sheet)
        {
            // 預設右下角內縮量：距離右緣 220mm（容納明細表寬度與右側圖框欄），距離下緣 180mm（預留下方文字與圖框標題欄）
            double offsetLeftFromRightEdgeFeet = UnitUtils.ConvertToInternalUnits(220.0, UnitTypeId.Millimeters);
            double offsetUpFromBottomEdgeFeet = UnitUtils.ConvertToInternalUnits(180.0, UnitTypeId.Millimeters);

            Element? titleBlock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            if (titleBlock != null)
            {
                BoundingBoxXYZ? tbBox = titleBlock.get_BoundingBox(sheet);
                if (tbBox != null)
                {
                    double x = tbBox.Max.X - offsetLeftFromRightEdgeFeet;
                    double y = tbBox.Min.Y + offsetUpFromBottomEdgeFeet;
                    return new XYZ(x, y, 0.0);
                }
            }

            BoundingBoxUV? outline = sheet.Outline;
            if (outline != null && outline.Max.U > outline.Min.U && outline.Max.V > outline.Min.V)
            {
                double x = outline.Max.U - offsetLeftFromRightEdgeFeet;
                double y = outline.Min.V + offsetUpFromBottomEdgeFeet;
                return new XYZ(x, y, 0.0);
            }

            // 若無圖框且 Outline 未定義，回傳標準 A1 圖紙右下角經驗座標
            return new XYZ(
                UnitUtils.ConvertToInternalUnits(550.0, UnitTypeId.Millimeters),
                UnitUtils.ConvertToInternalUnits(180.0, UnitTypeId.Millimeters),
                0.0);
        }

        /// <summary>
        /// 計算說明文字位置（預設放置於明細表正下方，對齊明細表左緣）
        /// </summary>
        private static XYZ ComputeTextNoteLocation(
            ViewSheet sheet,
            ScheduleSheetInstance scheduleInstance,
            XYZ schedulePoint)
        {
            double verticalGapFeet = UnitUtils.ConvertToInternalUnits(12.0, UnitTypeId.Millimeters);
            double fallbackDropFeet = UnitUtils.ConvertToInternalUnits(65.0, UnitTypeId.Millimeters);

            BoundingBoxXYZ? schedBox = scheduleInstance?.get_BoundingBox(sheet);
            if (schedBox != null && schedBox.Max.Y > schedBox.Min.Y)
            {
                return new XYZ(schedBox.Min.X, schedBox.Min.Y - verticalGapFeet, 0.0);
            }

            return new XYZ(schedulePoint.X, schedulePoint.Y - fallbackDropFeet, 0.0);
        }
    }
}
