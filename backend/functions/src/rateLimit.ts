import { createHash } from "node:crypto";

export interface CounterResult {
  allowed: boolean;
  count: number;
}

export interface CounterStore {
  /** Incrementa o contador do dia da identidade; recusa (sem incrementar) quando já atingiu o limite. */
  increment(identityKey: string, dayKey: string, limit: number): Promise<CounterResult>;
}

export const dayKey = (now: Date): string => now.toISOString().slice(0, 10).replaceAll("-", "");

/** Identidade: uid quando autenticado; caso contrário, hash do IP. Nada pessoal é gravado em texto claro. */
export function identityKey(uid: string | null, ip: string | undefined): string {
  const raw = uid ? `uid:${uid}` : `ip:${ip ?? "unknown"}`;
  return createHash("sha256").update(raw).digest("hex").slice(0, 32);
}

export class MemoryCounterStore implements CounterStore {
  private readonly counts = new Map<string, number>();
  async increment(identity: string, day: string, limit: number): Promise<CounterResult> {
    const key = `${day}_${identity}`;
    const current = this.counts.get(key) ?? 0;
    if (current >= limit) return { allowed: false, count: current };
    this.counts.set(key, current + 1);
    return { allowed: true, count: current + 1 };
  }
}

/** Subconjunto do Firestore Admin usado aqui (facilita testar sem emulador). */
export interface TxDb {
  runTransaction<T>(fn: (tx: TxLike) => Promise<T>): Promise<T>;
  doc(path: string): unknown;
}
export interface TxLike {
  get(ref: unknown): Promise<{ exists: boolean; data(): Record<string, unknown> | undefined }>;
  set(ref: unknown, data: Record<string, unknown>): unknown;
}

/** Contador diário em Firestore (coleção rateLimits, inacessível a clientes pelas Security Rules; só o Admin SDK escreve). */
export class FirestoreCounterStore implements CounterStore {
  constructor(private readonly db: TxDb, private readonly now: () => Date = () => new Date()) {}

  async increment(identity: string, day: string, limit: number): Promise<CounterResult> {
    const ref = this.db.doc(`rateLimits/${day}_${identity}`);
    return this.db.runTransaction(async (tx) => {
      const snap = await tx.get(ref);
      const current = snap.exists ? Number(snap.data()?.count ?? 0) : 0;
      if (current >= limit) return { allowed: false, count: current };
      const now = this.now();
      tx.set(ref, {
        count: current + 1,
        day,
        updatedAt: now,
        // Habilite uma política TTL do Firestore em "expiresAt" para limpar contadores antigos automaticamente.
        expiresAt: new Date(now.getTime() + 3 * 24 * 3600 * 1000),
      });
      return { allowed: true, count: current + 1 };
    });
  }
}

/** Limitador de rajada em memória (por instância): complementa o contador diário. */
export class BurstLimiter {
  private readonly hits = new Map<string, number[]>();
  constructor(private readonly max: number, private readonly windowMs: number, private readonly clock: () => number = Date.now) {}

  allow(key: string): boolean {
    const now = this.clock();
    const recent = (this.hits.get(key) ?? []).filter((t) => now - t < this.windowMs);
    if (recent.length >= this.max) {
      this.hits.set(key, recent);
      return false;
    }
    recent.push(now);
    this.hits.set(key, recent);
    return true;
  }
}
