import { LIMITS, MODEL, OPENROUTER_URL } from "./config";
import { AbortedError, UpstreamError } from "./errors";

export interface ChatMessage {
  role: "system" | "user";
  content: string;
}

export interface ChatOptions {
  apiKey: string;
  messages: ChatMessage[];
  /** Cancelamento vindo do cliente (conexão encerrada). */
  signal?: AbortSignal;
  fetchImpl?: typeof fetch;
  sleep?: (ms: number) => Promise<void>;
  timeoutMs?: number;
  maxRetries?: number;
  model?: string;
}

const defaultSleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/**
 * Chama o OpenRouter (modelo openrouter/free) com timeout, cancelamento, retry controlado (429/408/5xx/rede/resposta vazia),
 * backoff exponencial com jitter e validação rigorosa da resposta. Erros 4xx de configuração/entrada não sofrem retry.
 */
export async function chatCompletion(o: ChatOptions): Promise<string> {
  const fetchImpl = o.fetchImpl ?? fetch;
  const sleep = o.sleep ?? defaultSleep;
  const maxRetries = o.maxRetries ?? LIMITS.maxRetries;
  let lastError: UpstreamError = new UpstreamError("sem tentativas", true);

  for (let attempt = 0; attempt <= maxRetries; attempt++) {
    if (o.signal?.aborted) throw new AbortedError();
    try {
      return await singleAttempt(o, fetchImpl);
    } catch (e) {
      if (e instanceof AbortedError) throw e;
      if (!(e instanceof UpstreamError)) throw e;
      lastError = e;
      if (!e.retryable || attempt === maxRetries) break;
      const backoff = Math.min(250 * 2 ** attempt, 2000) + Math.floor(Math.random() * 150);
      await sleep(backoff);
    }
  }
  throw lastError;
}

async function singleAttempt(o: ChatOptions, fetchImpl: typeof fetch): Promise<string> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort("timeout"), o.timeoutMs ?? LIMITS.upstreamTimeoutMs);
  const onClientAbort = () => controller.abort("client");
  o.signal?.addEventListener("abort", onClientAbort, { once: true });

  try {
    const response = await fetchImpl(OPENROUTER_URL, {
      method: "POST",
      signal: controller.signal,
      headers: {
        Authorization: `Bearer ${o.apiKey}`,
        "Content-Type": "application/json",
        "X-Title": "Rhaghoorian",
      },
      body: JSON.stringify({
        model: o.model ?? MODEL,
        messages: o.messages,
        temperature: 0.2,
        max_tokens: LIMITS.maxOutputTokens,
      }),
    });

    if (!response.ok) {
      const retryable = response.status === 408 || response.status === 429 || response.status >= 500;
      throw new UpstreamError(`OpenRouter respondeu HTTP ${response.status}`, retryable, response.status);
    }

    let data: unknown;
    try {
      data = await response.json();
    } catch {
      throw new UpstreamError("Resposta do OpenRouter não é JSON", true);
    }
    return extractContent(data);
  } catch (e) {
    if (e instanceof UpstreamError) throw e;
    if (o.signal?.aborted) throw new AbortedError();
    // timeout ou falha de rede: tentar de novo
    throw new UpstreamError(controller.signal.aborted ? "timeout" : "falha de rede", true);
  } finally {
    clearTimeout(timeout);
    o.signal?.removeEventListener("abort", onClientAbort);
  }
}

/** Valida o formato OpenAI-compatível e devolve só o texto útil. */
export function extractContent(data: unknown): string {
  const message = (data as { choices?: Array<{ message?: { content?: unknown } }> } | null)?.choices?.[0]?.message;
  let content = message?.content;
  if (Array.isArray(content)) {
    content = content.map((p) => (typeof p === "string" ? p : typeof (p as { text?: unknown })?.text === "string" ? (p as { text: string }).text : "")).join("");
  }
  if (typeof content !== "string") throw new UpstreamError("Resposta sem conteúdo de texto", true);

  const text = content.replace(/<think>[\s\S]*?<\/think>/gi, "").trim();
  if (text.length === 0) throw new UpstreamError("Resposta vazia", true);
  if (text.length > LIMITS.maxOutputChars) throw new UpstreamError("Resposta longa demais", false);
  return text;
}
