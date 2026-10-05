import { LIMITS } from "./config";
import { AbortedError, HttpError, UpstreamError } from "./errors";
import { chatCompletion, type ChatMessage } from "./openrouter";
import { conjugationMessages, synonymMessages, translationMessages } from "./prompts";
import { BurstLimiter, dayKey, identityKey, type CounterStore } from "./rateLimit";
import { parseConjugate, parseSynonyms, parseTranslate } from "./validate";

export interface HttpReq {
  method: string;
  path: string;
  headers: Record<string, string | string[] | undefined>;
  body: unknown;
  ip?: string;
  on(event: "close", cb: () => void): unknown;
}
export interface HttpRes {
  status(code: number): HttpRes;
  json(body: unknown): unknown;
  set(name: string, value: string): unknown;
  readonly writableEnded?: boolean;
}

export interface HandlerDeps {
  /** Lê a chave do segredo no momento da chamada (nunca em tempo de import). */
  getApiKey(): string;
  /** Valida o ID token do Firebase Auth e devolve o uid, ou null se inválido. */
  verifyToken(idToken: string): Promise<string | null>;
  counters: CounterStore;
  burst: BurstLimiter;
  fetchImpl?: typeof fetch;
  sleep?: (ms: number) => Promise<void>;
  now?: () => Date;
  log(level: "info" | "warn" | "error", message: string, data?: Record<string, unknown>): void;
}

type Route = "translate" | "conjugate" | "synonyms";

/** Rotas que alteram o dicionário (cadastro) exigem login; o tradutor pode ser usado como visitante. */
const REQUIRES_LOGIN: Record<Route, boolean> = { translate: false, conjugate: true, synonyms: true };

export function createHandler(deps: HandlerDeps) {
  const now = deps.now ?? (() => new Date());

  return async function handle(req: HttpReq, res: HttpRes): Promise<void> {
    const started = Date.now();
    const controller = new AbortController();
    req.on("close", () => { if (!res.writableEnded) controller.abort(); });

    const send = (status: number, body: Record<string, unknown>) => { res.status(status).json(body); };
    let route: Route | undefined;
    let uid: string | null = null;

    try {
      if (req.method !== "POST") throw new HttpError(405, "Use POST.");
      const name = req.path.replace(/^\/+|\/+$/g, "").split("/").pop() as Route;
      if (!(name in REQUIRES_LOGIN)) throw new HttpError(404, "Rota não encontrada.");
      route = name;

      // 1) identidade
      const header = headerValue(req.headers.authorization);
      if (header) {
        const match = /^Bearer\s+(.+)$/i.exec(header);
        uid = match ? await deps.verifyToken(match[1]) : null;
        if (!uid) throw new HttpError(401, "Sessão inválida. Entre novamente.");
      }
      if (REQUIRES_LOGIN[route] && !uid) throw new HttpError(401, "Entre com sua conta para usar este recurso.");

      // 2) validação (antes de gastar cota)
      const messages: ChatMessage[] =
        route === "translate" ? translationMessages(parseTranslate(req.body))
        : route === "conjugate" ? conjugationMessages(parseConjugate(req.body))
        : synonymMessages(parseSynonyms(req.body));

      // 3) limites
      const identity = identityKey(uid, clientIp(req));
      if (!deps.burst.allow(identity)) throw new HttpError(429, "Muitas requisições seguidas. Aguarde um instante.", 30);
      const quota = await deps.counters.increment(identity, dayKey(now()), LIMITS.dailyPerIdentity);
      if (!quota.allowed) throw new HttpError(429, "Limite diário de usos da IA atingido. Tente novamente amanhã.", 3600);

      // 4) chamada ao OpenRouter
      const apiKey = deps.getApiKey();
      if (!apiKey) {
        deps.log("error", "OPENROUTER_API_KEY não configurada no ambiente do backend.");
        throw new HttpError(503, "A IA não está configurada.");
      }
      const text = await chatCompletion({ apiKey, messages, signal: controller.signal, fetchImpl: deps.fetchImpl, sleep: deps.sleep });

      deps.log("info", "ai_ok", { route, authenticated: uid !== null, ms: Date.now() - started, used: quota.count });
      send(200, { text });
    } catch (e) {
      if (e instanceof AbortedError) { deps.log("info", "ai_client_aborted", { route }); return; }
      if (e instanceof HttpError) {
        if (e.retryAfterSeconds) res.set("Retry-After", String(e.retryAfterSeconds));
        deps.log(e.status >= 500 ? "error" : "warn", "ai_rejected", { route, status: e.status });
        send(e.status, { error: e.message });
        return;
      }
      if (e instanceof UpstreamError) {
        // Detalhes técnicos só no log (sem chave e sem o texto do usuário).
        deps.log("error", "ai_upstream_failed", { route, upstreamStatus: e.status ?? null, reason: e.message, ms: Date.now() - started });
        send(503, { error: "A IA está indisponível no momento." });
        return;
      }
      deps.log("error", "ai_unexpected", { route, reason: e instanceof Error ? e.name : "unknown" });
      send(500, { error: "Erro inesperado." });
    }
  };
}

const headerValue = (v: string | string[] | undefined): string | undefined => (Array.isArray(v) ? v[0] : v);

function clientIp(req: HttpReq): string | undefined {
  const fwd = headerValue(req.headers["x-forwarded-for"]);
  return fwd ? fwd.split(",")[0].trim() : req.ip;
}
