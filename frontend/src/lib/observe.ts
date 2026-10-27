/** Unavailable credentials keep observations from being sent at all, rather than failing them. */
export function isCredentialBlock(error: string): boolean {
  return /^blocked credentials/i.test(error);
}
