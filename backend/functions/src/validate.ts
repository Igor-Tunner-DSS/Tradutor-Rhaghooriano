import { LIMITS } from "./config";
import { HttpError } from "./errors";

export type Direction = "pt-rh" | "rh-pt";

export interface ContextEntry {
  rhagho: string;
  portuguese: string;
  partOfSpeech: string;
  tense?: string;
}
export interface TranslateBody {
  text: string;
  direction: Direction;
  entries: ContextEntry[];
  unknownWords: string[];
  localDraft?: string;
}
export interface ConjugateBody {
  infinitive: string;
  baseForm: string;
}
export interface SynonymsBody {
  portuguese: string;
  rhagho?: string;
  partOfSpeech?: string;
  existing: string[];
}

const bad = (msg: string): never => {
  throw new HttpError(400, msg);
};

function obj(body: unknown): Record<string, unknown> {
  if (typeof body !== "object" || body === null || Array.isArray(body)) bad("Corpo da requisição inválido.");
  return body as Record<string, unknown>;
}

function str(v: unknown, name: string, max: number, required = true): string {
  if (v === undefined || v === null) {
    if (required) bad(`Campo ausente: ${name}.`);
    return "";
  }
  if (typeof v !== "string") bad(`Campo inválido: ${name}.`);
  const s = (v as string).trim();
  if (required && s.length === 0) bad(`Campo vazio: ${name}.`);
  if (s.length > max) bad(`Campo muito longo: ${name}.`);
  return s;
}

function strList(v: unknown, name: string, maxItems: number, maxLen: number): string[] {
  if (v === undefined || v === null) return [];
  if (!Array.isArray(v)) return bad(`Campo inválido: ${name}.`);
  if (v.length > maxItems) bad(`Itens demais em ${name}.`);
  return v.map((x, i) => str(x, `${name}[${i}]`, maxLen));
}

export function parseTranslate(body: unknown): TranslateBody {
  const o = obj(body);
  const direction = o.direction;
  if (direction !== "pt-rh" && direction !== "rh-pt") bad("Direção inválida.");

  const ctx = o.context === undefined ? {} : obj(o.context);
  const rawEntries = ctx.entries ?? [];
  if (!Array.isArray(rawEntries)) bad("Contexto inválido.");
  if ((rawEntries as unknown[]).length > LIMITS.maxContextEntries) bad("Contexto grande demais.");

  const entries = (rawEntries as unknown[]).map((e, i) => {
    const eo = obj(e);
    const tense = eo.tense === undefined || eo.tense === null ? undefined : str(eo.tense, `entries[${i}].tense`, 20);
    return {
      rhagho: str(eo.rhagho, `entries[${i}].rhagho`, LIMITS.maxFieldChars),
      portuguese: str(eo.portuguese, `entries[${i}].portuguese`, LIMITS.maxFieldChars),
      partOfSpeech: str(eo.partOfSpeech, `entries[${i}].partOfSpeech`, 40, false),
      tense,
    } satisfies ContextEntry;
  });

  return {
    text: str(o.text, "text", LIMITS.maxTextChars),
    direction: direction as Direction,
    entries,
    unknownWords: strList(ctx.unknownWords, "unknownWords", LIMITS.maxUnknownWords, LIMITS.maxFieldChars),
    localDraft: o.localDraft == null ? undefined : str(o.localDraft, "localDraft", LIMITS.maxTextChars * 2, false),
  };
}

export function parseConjugate(body: unknown): ConjugateBody {
  const o = obj(body);
  return {
    infinitive: str(o.infinitive, "infinitive", 60),
    baseForm: str(o.baseForm, "baseForm", 80),
  };
}

export function parseSynonyms(body: unknown): SynonymsBody {
  const o = obj(body);
  return {
    portuguese: str(o.portuguese, "portuguese", LIMITS.maxFieldChars),
    rhagho: o.rhagho == null ? undefined : str(o.rhagho, "rhagho", LIMITS.maxFieldChars, false),
    partOfSpeech: o.partOfSpeech == null ? undefined : str(o.partOfSpeech, "partOfSpeech", 40, false),
    existing: strList(o.existing, "existing", 30, LIMITS.maxFieldChars),
  };
}
