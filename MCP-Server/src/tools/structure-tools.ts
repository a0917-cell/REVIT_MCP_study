/**
 * 結構分析工具 — structure Profile
 */

import { Tool } from "@modelcontextprotocol/sdk/types.js";

export const structureTools: Tool[] = [
    {
        name: "analyze_beam_penetration",
        description: "分析特定結構梁上的套管穿孔。回傳精確的幾何數據，如距離柱心長度、梁深度、開孔直徑等。",
        inputSchema: {
            type: "object",
            properties: {
                beamId: { type: "number", description: "要分析的目標結構梁 Element ID" },
                diameterParamNames: { type: "array", items: { type: "string" }, description: "可選。搜尋套管『直徑』的動態參數名稱清單（實體與類型自動 Fallback）。預設為 ['開孔直徑', '直徑', '管徑', 'Diameter', 'Size']" },
                lengthParamNames: { type: "array", items: { type: "string" }, description: "可選。搜尋套管『長度』的動態參數名稱清單。預設為 ['長度', 'Length']" },
                widthParamNames: { type: "array", items: { type: "string" }, description: "可選。搜尋梁『寬度』的動態參數名稱清單。預設為 ['b', '梁寬', 'Width']" },
                sleeveIds: { type: "array", items: { type: "number" }, description: "可選。指定檢核的套管 ID 清單，避免全區掃描" },
                linkInstanceId: { type: "number", description: "可選。連結模型的 ID" }
            },
            required: ["beamId"],
        },
    },
    {
        name: "scan_penetrated_beams_in_view",
        description: "掃描目前視圖中所有被套管（Sleeves）穿過的結構梁。回傳包含梁 ID、連結模型 ID 及穿過該梁的套管數量的清單。",
        inputSchema: {
            type: "object",
            properties: {},
        },
    },
    {
        name: "align_structural_framing",
        description: "自動將結構構架（梁）端點的實體延伸切齊到相接的樑面或柱面，含連結檔 (RVT Links) 中的構材。只寫起點/終點延伸參數，構架的軸線位置不會被移動。相接構材貫穿本樑（T 接）時退到近端面貼齊；相接構材在此終止（L 轉角）時延伸到遠端面。節點過於繁忙（相接候選超過 4 個）的端點會跳過不動並回報，需人工確認。支援單一/多支構架或當前視圖中的所有結構構架。",
        inputSchema: {
            type: "object",
            properties: {
                framingIds: { type: "array", items: { type: "number" }, description: "可選。指定要對齊的結構構架 Element ID 清單。若未指定，則自動對齊當前視圖內的所有結構構架。" },
                targetCategoryTypes: { type: "array", items: { type: "string" }, description: "可選。對齊目標類別清單，可用值 'framing' / 'column' / 'floor' / 'wall'。預設為 ['framing', 'column']；'floor' 與 'wall' 不在預設內，因為樓板 bbox 中心可能離樑端十幾公尺，實測會把樑對到離譜位置。" },
                maxDistanceMm: { type: "number", description: "可選。沿樑軸的搜尋窗口（毫米 mm），預設 700.0 mm。此值經真模型校準：容得下相接大梁半寬與 600 柱半寬，同時排除軸線外約 900 mm、實際並未相接的柱。調大可能重新引入該誤判。" },
                disallowJoinAtEnds: { type: "boolean", description: "可選。對齊後是否自動禁用端點自動接合 (Disallow Join)，避免 Revit 自動縮回，預設為 true。" },
                viewId: { type: "number", description: "可選。指定視圖 Element ID，預設為當前啟用視圖。" }
            },
        },
    },
];

