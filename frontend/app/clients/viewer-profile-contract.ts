import { z } from "zod";

export const viewerProfileSchema = z.strictObject({
  id: z.number().int().positive(),
  name: z.string(),
});
export type ViewerProfile = z.infer<typeof viewerProfileSchema>;

export const viewerProfilesResponseSchema = z.strictObject({
  status: z.literal(true),
  error: z.string().nullable().optional(),
  items: z.array(viewerProfileSchema),
});

export const viewerProfileResponseSchema = z.strictObject({
  status: z.literal(true),
  error: z.string().nullable().optional(),
  profile: viewerProfileSchema,
});
