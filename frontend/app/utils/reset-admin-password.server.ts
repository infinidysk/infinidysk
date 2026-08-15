export function isResetAdminPasswordSet(): boolean {
  const value = process.env["RESET_ADMIN_PASSWORD"]?.trim().toLowerCase();
  return value === "true" || value === "1" || value === "yes" || value === "y";
}
