/**
 * 房間避難路徑法規檢核與圖紙排版工具 (Egress Audit & Sheet Note Tools)
 */

import { z } from "zod";
import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * Zod 輸入驗證 Schema：audit_room_egress
 */
export const auditRoomEgressInputSchema = z.object({
    roomIds: z
        .array(z.number())
        .min(1, "roomIds 至少需包含一個房間 ElementId")
        .describe("欲檢核的房間 ElementId 清單"),
    exitDoorIds: z
        .array(z.number())
        .min(1, "exitDoorIds 至少需包含一個避難出口門 ElementId")
        .describe("作為避難出口的門 ElementId 清單"),
    maxAllowedMeters: z
        .number()
        .positive("maxAllowedMeters 必須大於 0")
        .describe("法規容許最大步行距離公尺數"),
    dimensionSegments: z
        .boolean()
        .optional()
        .default(true)
        .describe("是否自動於逃生路徑每個轉折線段（>= 10cm）上建立 Revit 原生線性尺寸標註（預設為 true）"),
});

export type AuditRoomEgressInput = z.infer<typeof auditRoomEgressInputSchema>;

/**
 * Zod 輸入驗證 Schema：generate_egress_sheet_note
 */
export const generateEgressSheetNoteInputSchema = z.object({
    sheetNumber: z
        .string()
        .min(1, "sheetNumber 不可為空")
        .describe('要放置檢討表與備註的目標圖號，例如 "A-101"'),
    regulationNoteText: z
        .string()
        .describe("AI 根據建築技術規則自動彙整的法規說明與結論文字，支援換行符號"),
});

export type GenerateEgressSheetNoteInput = z.infer<typeof generateEgressSheetNoteInputSchema>;

export const egressAuditTools: Tool[] = [
    {
        name: "audit_room_egress",
        description:
            "執行房間避難步行距離法規檢核。自動建立房間避難共用參數、由房間外圈頂點內縮 0.3m 計算至各避難出口門的最佳逃生路徑並保留最不利路徑、寫入合格狀態參數、套用視圖紅綠線型覆寫、中點標籤，並於每個轉折線段（>= 10cm）建立 Revit 原生線性尺寸標註。",
        inputSchema: {
            type: "object",
            properties: {
                roomIds: {
                    type: "array",
                    items: { type: "number" },
                    description: "欲檢核的房間 ElementId 清單",
                },
                exitDoorIds: {
                    type: "array",
                    items: { type: "number" },
                    description: "作為避難出口的門 ElementId 清單",
                },
                maxAllowedMeters: {
                    type: "number",
                    description: "法規容許最大步行距離公尺數",
                },
                dimensionSegments: {
                    type: "boolean",
                    description:
                        "是否自動於逃生路徑每個轉折線段（>= 10cm）上建立 Revit 原生線性尺寸標註（預設 true）",
                },
            },
            required: ["roomIds", "exitDoorIds", "maxAllowedMeters"],
        },
    },
    {
        name: "generate_egress_sheet_note",
        description:
            "於指定圖紙建立房間避難步行距離檢討表並放置法規檢討綜整說明文字。自動建立包含房間編號、名稱與 4 個避難共用參數的明細表，並排版於圖紙右下角及下方文字備註。",
        inputSchema: {
            type: "object",
            properties: {
                sheetNumber: {
                    type: "string",
                    description: '要放置檢討表與備註的目標圖號，例如 "A-101"',
                },
                regulationNoteText: {
                    type: "string",
                    description: "AI 根據建築技術規則自動彙整的法規說明與結論文字，支援換行符號",
                },
            },
            required: ["sheetNumber", "regulationNoteText"],
        },
    },
];

/**
 * 將 C# 端回傳的房間避難檢核結果格式化為易讀報告與結構化資料
 */
export function formatEgressAuditResponse(data: any): string {
    if (!data || typeof data !== "object") {
        return JSON.stringify(data, null, 2);
    }

    const totalRooms = data.TotalRooms ?? 0;
    const compliantCount = data.CompliantCount ?? 0;
    const nonCompliantCount = data.NonCompliantCount ?? 0;
    const failedCount = data.FailedCount ?? 0;
    const maxAllowedMeters = data.MaxAllowedMeters ?? 0;
    const results: any[] = Array.isArray(data.Results) ? data.Results : [];

    const lines: string[] = [
        `=== 房間避難步行距離檢核報告 ===`,
        `法規容許上限：${maxAllowedMeters} m`,
        `檢核房間總數：${totalRooms} 間（合格：${compliantCount}｜不合格：${nonCompliantCount}｜失敗：${failedCount}）`,
        ``,
        `--- 各房間檢核明細 ---`,
    ];

    for (const item of results) {
        const roomLabel = `${item.RoomNumber ? `[${item.RoomNumber}] ` : ""}${item.RoomName || "未命名房間"} (ID: ${item.RoomId})`;
        if (!item.Success) {
            lines.push(`❌ ${roomLabel}：檢核失敗 — ${item.Error || "未知錯誤"}`);
            continue;
        }

        const badge = item.IsCompliant === "合格" ? "✅ [合格]" : "🚨 [不合格]";
        const segLengths: number[] = Array.isArray(item.SegmentLengthsMeters) ? item.SegmentLengthsMeters : [];
        const segDimIds: number[] = Array.isArray(item.SegmentDimensionIds) ? item.SegmentDimensionIds : [];
        const segSummary =
            segLengths.length > 0
                ? `｜各轉折線段：${segLengths.map((len) => `${len} m`).join(" + ")}（已標註 ${segDimIds.length} 段）`
                : "";
        lines.push(
            `${badge} ${roomLabel}：實測最遠步行距離 ${item.ActualDistanceMeters} m / 上限 ${item.MaxAllowedMeters} m｜最佳逃生出口：${item.TargetExit}｜路徑 ID: ${item.PathOfTravelId}${item.TagId ? `｜標籤 ID: ${item.TagId}` : ""}${segSummary}`
        );
    }

    lines.push(``, `--- 原始 JSON 資料 ---`, JSON.stringify(data, null, 2));
    return lines.join("\n");
}

/**
 * 將 C# 端回傳的圖紙自動排版結果格式化回傳給 AI
 */
export function formatGenerateEgressSheetNoteResponse(sheetNumber: string, data: any): string {
    const resolvedSheetNumber = data?.SheetNumber || sheetNumber;
    const header = `已成功於圖紙 ${resolvedSheetNumber} 建立法規檢討明細表與備註`;

    if (!data || typeof data !== "object") {
        return header;
    }

    return `${header}\n\n--- 詳細資訊 ---\n${JSON.stringify(data, null, 2)}`;
}
