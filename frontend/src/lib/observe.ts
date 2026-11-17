/** Why an installation's observation was not sent, rather than failed: the server's ObserveBlockReasons. */
export const observeBlockReasons = {
  /** A credential it needs is unavailable: nothing was sent, and the fix is in Variables. */
  credentials: "credentials",
} as const;
