export function isPositiveInteger(value: string): boolean {
  const number = Number(value);
  return Number.isInteger(number) && number > 0 && value.trim() === number.toString();
}

export function isPositiveNumber(value: string): boolean {
  const trimmed = value.trim();
  if (!/^\d+(\.\d+)?$/.test(trimmed)) return false;
  return Number(trimmed) > 0;
}
