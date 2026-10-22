/**
 * 房間避難路徑法規檢核工具 (Egress Audit Tools)
 */

import { z } from "zod";
import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * Zod 輸入驗證 Schema
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
});

export type AuditRoomEgressInput = z.infer<typeof auditRoomEgressInputSchema>;

export const egressAuditTools: Tool[] = [
    {
        name: "audit_room_egress",
        description:
            "執行房間避難步行距離法規檢核。自動建立房間避難共用參數、由房間外圈頂點內縮 0.3m 計算至各避難出口門的最佳逃生路徑並保留最不利路徑、寫入合格狀態參數、套用視圖紅綠線型覆寫與中點標籤。",
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
            },
            required: ["roomIds", "exitDoorIds", "maxAllowedMeters"],
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
        lines.push(
            `${badge} ${roomLabel}：實測最遠步行距離 ${item.ActualDistanceMeters} m / 上限 ${item.MaxAllowedMeters} m｜最佳逃生出口：${item.TargetExit}｜路徑 ID: ${item.PathOfTravelId}${item.TagId ? `｜標籤 ID: ${item.TagId}` : ""}`
        );
    }

    lines.push(``, `--- 原始 JSON 資料 ---`, JSON.stringify(data, null, 2));
    return lines.join("\n");
}
