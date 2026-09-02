export const PRODUCT_ID = "KnowledgeClipper" as const;
export const PRODUCT_DISPLAY_NAME = "零碎知识剪藏" as const;
export const MODULE_ROLE = "content-understanding-and-card-generation" as const;

export const DESTINATION_NEUTRAL_METADATA = {
  productId: PRODUCT_ID,
  productDisplayName: PRODUCT_DISPLAY_NAME,
  moduleRole: MODULE_ROLE,
  destinationNeutral: true,
} as const;
