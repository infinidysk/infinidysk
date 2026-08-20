const SECRET_MASK_PREFIX = "__NZBDAV_SECRET_MASK_V1__:";

export function isMaskedSecret(value: string | undefined): boolean {
  return value?.startsWith(SECRET_MASK_PREFIX) ?? false;
}
