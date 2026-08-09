export function isPositiveInteger(value: string): boolean {
  const number = Number(value);
  return Number.isInteger(number) && number > 0 && value.trim() === number.toString();
}
