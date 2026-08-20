/**
 * Generates a UUID v4 in both secure and insecure browser contexts.
 * `crypto.randomUUID` requires a secure context, but self-hosted UIs are often
 * served over plain HTTP on a LAN; `crypto.getRandomValues` remains available.
 */
export function generateUuid(): string {
  const browserCrypto = globalThis.crypto;
  if (!browserCrypto) {
    throw new Error(
      "This browser does not provide the cryptographic random source required to generate a UUID.",
    );
  }

  if (typeof browserCrypto.randomUUID === "function") {
    return browserCrypto.randomUUID();
  }
  if (typeof browserCrypto.getRandomValues !== "function") {
    throw new Error(
      "This browser does not provide the cryptographic random source required to generate a UUID.",
    );
  }

  const bytes = new Uint8Array(16);
  browserCrypto.getRandomValues(bytes);
  // bytes is a 16-element array; indices 6 and 8 are always in bounds.
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
