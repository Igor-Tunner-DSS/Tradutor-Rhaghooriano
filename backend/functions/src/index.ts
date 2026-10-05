import { initializeApp } from "firebase-admin/app";
import { getAuth } from "firebase-admin/auth";
import { getFirestore } from "firebase-admin/firestore";
import { logger } from "firebase-functions";
import { defineSecret } from "firebase-functions/params";
import { onRequest } from "firebase-functions/v2/https";
import { LIMITS } from "./config";
import { createHandler } from "./handler";
import { BurstLimiter, FirestoreCounterStore, type TxDb } from "./rateLimit";

initializeApp();

/**
 * A chave do OpenRouter vive somente aqui, no Secret Manager do Firebase:
 *   firebase functions:secrets:set OPENROUTER_API_KEY
 * (para o emulador local, use backend/functions/.secret.local — ignorado pelo git).
 */
const openrouterKey = defineSecret("OPENROUTER_API_KEY");

const db = getFirestore();

const handler = createHandler({
  getApiKey: () => openrouterKey.value(),
  verifyToken: async (token) => {
    try {
      return (await getAuth().verifyIdToken(token)).uid;
    } catch {
      return null;
    }
  },
  counters: new FirestoreCounterStore(db as unknown as TxDb),
  burst: new BurstLimiter(LIMITS.burstPerMinute, 60_000),
  log: (level, message, data) => logger[level](message, data ?? {}),
});

/** POST https://<regiao>-<projeto>.cloudfunctions.net/ai/{translate|conjugate|synonyms} */
export const ai = onRequest(
  { secrets: [openrouterKey], cors: false, timeoutSeconds: 90, memory: "256MiB", maxInstances: 5 },
  (req, res) => handler(req as never, res as never),
);
