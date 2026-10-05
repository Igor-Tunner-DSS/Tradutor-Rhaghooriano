/** Configuração fixa da camada de IA. A chave do OpenRouter NÃO está aqui: ela vem do Secret Manager (ver index.ts). */
export const MODEL = "openrouter/free";
export const OPENROUTER_URL = "https://openrouter.ai/api/v1/chat/completions";

export const LIMITS = {
  /** ~20 usos de IA por dia por identidade (uid, ou origem para visitantes). Ajustável por variável de ambiente DAILY_LIMIT. */
  dailyPerIdentity: Number(process.env.DAILY_LIMIT ?? 20),
  /** Proteção contra rajadas dentro de uma mesma instância. */
  burstPerMinute: Number(process.env.BURST_PER_MINUTE ?? 6),
  maxTextChars: 2000,
  maxContextEntries: 80,
  maxUnknownWords: 30,
  maxFieldChars: 200,
  maxOutputChars: 4000,
  maxOutputTokens: 700,
  upstreamTimeoutMs: 40_000,
  maxRetries: 2,
} as const;
