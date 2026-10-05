export class HttpError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly retryAfterSeconds?: number,
  ) {
    super(message);
  }
}

/** Falha da chamada ao OpenRouter. Os detalhes técnicos ficam só no log; ao cliente vai uma mensagem genérica. */
export class UpstreamError extends Error {
  constructor(
    message: string,
    public readonly retryable: boolean,
    public readonly status?: number,
  ) {
    super(message);
  }
}

export class AbortedError extends Error {
  constructor() {
    super("aborted");
  }
}
