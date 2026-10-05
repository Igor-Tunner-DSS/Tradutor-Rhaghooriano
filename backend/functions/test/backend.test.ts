import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { LIMITS, MODEL } from "../src/config";
import { createHandler, type HandlerDeps, type HttpReq, type HttpRes } from "../src/handler";
import { HttpError, UpstreamError } from "../src/errors";
import { chatCompletion, extractContent } from "../src/openrouter";
import { conjugationMessages, synonymMessages, translationMessages } from "../src/prompts";
import { BurstLimiter, dayKey, FirestoreCounterStore, identityKey, MemoryCounterStore } from "../src/rateLimit";
import { parseConjugate, parseSynonyms, parseTranslate } from "../src/validate";

const SECRET = "sk-or-v1-SEGREDO-NUNCA-VAZAR";
const ok = (content: string) => new Response(JSON.stringify({ choices: [{ message: { content } }] }), { status: 200 });
const noSleep = async () => {};

describe("validação", () => {
  const base = { text: "nuruh", direction: "rh-pt", context: { entries: [{ rhagho: "nuruh", portuguese: "eu", partOfSpeech: "Pronome" }], unknownWords: ["zzz"] } };

  it("aceita um corpo válido", () => {
    const b = parseTranslate(base);
    assert.equal(b.entries.length, 1);
    assert.deepEqual(b.unknownWords, ["zzz"]);
  });
  it("rejeita direção, texto vazio, tipos errados e excesso de tamanho", () => {
    assert.throws(() => parseTranslate({ ...base, direction: "xx" }), HttpError);
    assert.throws(() => parseTranslate({ ...base, text: "  " }), HttpError);
    assert.throws(() => parseTranslate({ ...base, text: 42 }), HttpError);
    assert.throws(() => parseTranslate({ ...base, text: "a".repeat(LIMITS.maxTextChars + 1) }), HttpError);
    assert.throws(() => parseTranslate("string"), HttpError);
    const many = Array.from({ length: LIMITS.maxContextEntries + 1 }, () => base.context.entries[0]);
    assert.throws(() => parseTranslate({ ...base, context: { entries: many } }), HttpError);
  });
  it("valida conjugação e sinônimos", () => {
    assert.deepEqual(parseConjugate({ infinitive: "amar", baseForm: "mor'hk" }), { infinitive: "amar", baseForm: "mor'hk" });
    assert.throws(() => parseConjugate({ infinitive: "amar" }), HttpError);
    assert.equal(parseSynonyms({ portuguese: "casa", existing: ["lar"] }).existing[0], "lar");
    assert.throws(() => parseSynonyms({ portuguese: "" }), HttpError);
  });
});

describe("prompts", () => {
  it("tradução: exige saída só com o resultado, traz as regras verbais e o contexto compacto", () => {
    const [sys, user] = translationMessages(parseTranslate({
      text: "nuruh hoari'vil", direction: "rh-pt",
      context: { entries: [{ rhagho: "nuruh", portuguese: "eu", partOfSpeech: "Pronome" }], unknownWords: ["zzz"] }, localDraft: "Eu irei",
    }));
    assert.match(sys.content, /SOMENTE/);
    assert.match(sys.content, /'ta/);
    assert.match(sys.content, /'vil/);
    assert.match(sys.content, /'nux/);
    assert.match(sys.content, /NUNCA invente/);
    assert.match(user.content, /- nuruh = eu \(Pronome\)/);
    assert.match(user.content, /zzz/);
    assert.match(user.content, /<<<\nnuruh hoari'vil\n>>>/);
  });
  it("conjugação e sinônimos pedem formato restrito", () => {
    assert.match(conjugationMessages({ infinitive: "amar", baseForm: "mor'hk" })[0].content, /quatro linhas/);
    assert.match(synonymMessages({ portuguese: "casa", existing: ["lar"] })[0].content, /no máximo 8/);
  });
});

describe("OpenRouter", () => {
  const opts = (fetchImpl: typeof fetch, extra = {}) => ({ apiKey: SECRET, messages: [{ role: "user" as const, content: "oi" }], fetchImpl, sleep: noSleep, ...extra });

  it("usa o modelo openrouter/free e envia a chave só no header Authorization", async () => {
    let seen: { url: string; init: RequestInit } | undefined;
    const text = await chatCompletion(opts((async (url: string, init: RequestInit) => { seen = { url, init }; return ok("  Eu irei  "); }) as never));
    assert.equal(text, "Eu irei");
    assert.equal(MODEL, "openrouter/free");
    assert.equal(JSON.parse(seen!.init.body as string).model, "openrouter/free");
    assert.equal((seen!.init.headers as Record<string, string>).Authorization, `Bearer ${SECRET}`);
    assert.ok(!(seen!.init.body as string).includes(SECRET));
  });
  it("faz retry controlado em 503/429 e depois tem sucesso", async () => {
    let calls = 0;
    const text = await chatCompletion(opts((async () => (++calls < 3 ? new Response("x", { status: calls === 1 ? 503 : 429 }) : ok("pronto"))) as never));
    assert.equal(text, "pronto");
    assert.equal(calls, 3);
  });
  it("desiste após maxRetries e não faz retry em erro 401/402", async () => {
    let calls = 0;
    await assert.rejects(chatCompletion(opts((async () => { calls++; return new Response("x", { status: 500 }); }) as never)), UpstreamError);
    assert.equal(calls, LIMITS.maxRetries + 1);
    calls = 0;
    await assert.rejects(chatCompletion(opts((async () => { calls++; return new Response("x", { status: 401 }); }) as never)), (e: UpstreamError) => !e.retryable);
    assert.equal(calls, 1);
  });
  it("trata resposta vazia/inválida como falha com retry e remove <think>", async () => {
    let calls = 0;
    const text = await chatCompletion(opts((async () => (++calls === 1 ? ok("   ") : ok("<think>pensando</think>Resposta"))) as never));
    assert.equal(text, "Resposta");
    assert.throws(() => extractContent({ choices: [] }), UpstreamError);
    assert.throws(() => extractContent({ choices: [{ message: { content: 123 } }] }), UpstreamError);
    assert.throws(() => extractContent({ choices: [{ message: { content: "a".repeat(LIMITS.maxOutputChars + 1) } }] }), UpstreamError);
  });
  it("respeita timeout", async () => {
    const hang = ((_u: string, init: RequestInit) => new Promise((_r, rej) => init.signal!.addEventListener("abort", () => rej(new Error("abort"))))) as never;
    await assert.rejects(chatCompletion(opts(hang, { timeoutMs: 20, maxRetries: 0 })), (e: UpstreamError) => e.message === "timeout");
  });
  it("cancela quando o cliente desiste", async () => {
    const ctrl = new AbortController();
    const hang = ((_u: string, init: RequestInit) => new Promise((_r, rej) => init.signal!.addEventListener("abort", () => rej(new Error("abort"))))) as never;
    const p = chatCompletion(opts(hang, { signal: ctrl.signal }));
    setTimeout(() => ctrl.abort(), 10);
    await assert.rejects(p, (e: Error) => e.message === "aborted");
  });
});

describe("limites", () => {
  it("contador diário bloqueia no 21º uso e reinicia no dia seguinte", async () => {
    const store = new MemoryCounterStore();
    const id = identityKey("u1", undefined);
    for (let i = 1; i <= 20; i++) assert.equal((await store.increment(id, "20261003", 20)).allowed, true);
    const blocked = await store.increment(id, "20261003", 20);
    assert.equal(blocked.allowed, false);
    assert.equal(blocked.count, 20);
    assert.equal((await store.increment(id, "20261004", 20)).allowed, true);
    assert.equal((await store.increment(identityKey("u2", undefined), "20261003", 20)).allowed, true);
  });
  it("identidade usa hash (sem uid/IP em claro) e separa visitante de usuário", () => {
    assert.notEqual(identityKey("u1", "1.1.1.1"), identityKey(null, "1.1.1.1"));
    assert.ok(!identityKey(null, "1.1.1.1").includes("1.1.1.1"));
    assert.equal(dayKey(new Date("2026-10-03T12:00:00Z")), "20261003");
  });
  it("rajada: bloqueia e libera após a janela", () => {
    let t = 0;
    const b = new BurstLimiter(2, 1000, () => t);
    assert.ok(b.allow("k")); assert.ok(b.allow("k")); assert.ok(!b.allow("k"));
    t = 1001;
    assert.ok(b.allow("k"));
  });
  it("FirestoreCounterStore usa transação e não incrementa acima do limite", async () => {
    const data = new Map<string, Record<string, unknown>>();
    const db = {
      doc: (p: string) => p,
      runTransaction: async <T,>(fn: (tx: never) => Promise<T>) => fn({
        get: async (ref: string) => ({ exists: data.has(ref), data: () => data.get(ref) }),
        set: (ref: string, v: Record<string, unknown>) => { data.set(ref, v); },
      } as never),
    };
    const store = new FirestoreCounterStore(db as never, () => new Date("2026-10-03T00:00:00Z"));
    assert.equal((await store.increment("abc", "20261003", 1)).allowed, true);
    assert.equal((await store.increment("abc", "20261003", 1)).allowed, false);
    assert.equal(data.get("rateLimits/20261003_abc")!.count, 1);
    assert.ok(data.get("rateLimits/20261003_abc")!.expiresAt instanceof Date);
  });
});

describe("handler HTTP", () => {
  interface Out { status: number; body: any; headers: Record<string, string> }

  function setup(over: Partial<HandlerDeps> = {}) {
    const logs: string[] = [];
    const calls = { upstream: 0 };
    const deps: HandlerDeps = {
      getApiKey: () => SECRET,
      verifyToken: async (t) => (t === "good" ? "uid-1" : null),
      counters: new MemoryCounterStore(),
      burst: new BurstLimiter(100, 60_000),
      fetchImpl: (async () => { calls.upstream++; return ok("Eu irei caçar"); }) as never,
      sleep: noSleep,
      now: () => new Date("2026-10-03T10:00:00Z"),
      log: (l, m, d) => logs.push(`${l} ${m} ${JSON.stringify(d ?? {})}`),
      ...over,
    };
    const handler = createHandler(deps);
    const call = async (path: string, body: unknown, headers: Record<string, string> = {}, method = "POST"): Promise<Out> => {
      const out: Out = { status: 0, body: null, headers: {} };
      const res: HttpRes = {
        status(c) { out.status = c; return res; },
        json(b) { out.body = b; return res; },
        set(n, v) { out.headers[n] = v; },
      };
      await handler({ method, path, headers, body, ip: "9.9.9.9", on: () => undefined } as HttpReq, res);
      return out;
    };
    return { call, logs, calls, deps };
  }

  const tbody = { text: "nuruh hoari'vil", direction: "rh-pt", context: { entries: [], unknownWords: ["x"] } };

  it("visitante pode usar o tradutor e recebe somente { text }", async () => {
    const { call } = setup();
    const r = await call("/translate", tbody);
    assert.equal(r.status, 200);
    assert.deepEqual(r.body, { text: "Eu irei caçar" });
  });
  it("conjugação e sinônimos exigem login", async () => {
    const { call, calls } = setup();
    assert.equal((await call("/conjugate", { infinitive: "amar", baseForm: "mor'hk" })).status, 401);
    assert.equal((await call("/synonyms", { portuguese: "casa" })).status, 401);
    assert.equal(calls.upstream, 0);
    assert.equal((await call("/synonyms", { portuguese: "casa" }, { authorization: "Bearer good" })).status, 200);
  });
  it("token inválido → 401 e nenhuma chamada externa", async () => {
    const { call, calls } = setup();
    assert.equal((await call("/translate", tbody, { authorization: "Bearer bad" })).status, 401);
    assert.equal((await call("/translate", tbody, { authorization: "Basic abc" })).status, 401);
    assert.equal(calls.upstream, 0);
  });
  it("método/rota/corpo inválidos não gastam cota nem chamam o OpenRouter", async () => {
    const { call, calls } = setup();
    assert.equal((await call("/translate", tbody, {}, "GET")).status, 405);
    assert.equal((await call("/outra", tbody)).status, 404);
    assert.equal((await call("/translate", { text: "" })).status, 400);
    assert.equal(calls.upstream, 0);
  });
  it("limite diário: o 21º pedido recebe 429 com Retry-After", async () => {
    const { call, calls } = setup();
    for (let i = 0; i < 20; i++) assert.equal((await call("/translate", tbody, { authorization: "Bearer good" })).status, 200);
    const r = await call("/translate", tbody, { authorization: "Bearer good" });
    assert.equal(r.status, 429);
    assert.ok(r.headers["Retry-After"]);
    assert.equal(calls.upstream, 20);
  });
  it("falha do OpenRouter vira 503 genérico, sem vazar detalhes nem a chave", async () => {
    const { call, logs } = setup({ fetchImpl: (async () => new Response(`erro interno ${SECRET}`, { status: 500 })) as never });
    const r = await call("/translate", tbody);
    assert.equal(r.status, 503);
    assert.equal(JSON.stringify(r.body).includes(SECRET), false);
    assert.equal(logs.join("\n").includes(SECRET), false);
  });
  it("sem segredo configurado: 503 claro e log de erro (sem expor nada)", async () => {
    const { call, logs } = setup({ getApiKey: () => "" });
    const r = await call("/translate", tbody);
    assert.equal(r.status, 503);
    assert.match(logs.join("\n"), /OPENROUTER_API_KEY/);
  });
  it("logs não contêm o texto do usuário nem a chave", async () => {
    const { call, logs } = setup();
    await call("/translate", { ...tbody, text: "texto-secreto-do-usuario" });
    const all = logs.join("\n");
    assert.equal(all.includes("texto-secreto-do-usuario"), false);
    assert.equal(all.includes(SECRET), false);
  });
});
