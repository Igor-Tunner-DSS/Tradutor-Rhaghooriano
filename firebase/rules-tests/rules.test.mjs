// Executa as Security Rules contra o emulador do Firestore (requer firebase-tools + Java).
// Uso: cd firebase/rules-tests && npm install && npm test   (a partir da raiz: firebase.json é encontrado automaticamente)
import { initializeTestEnvironment, assertFails, assertSucceeds } from "@firebase/rules-unit-testing";
import { doc, getDoc, setDoc, deleteDoc, updateDoc, serverTimestamp, Timestamp } from "firebase/firestore";
import { readFileSync } from "node:fs";
import { after, before, beforeEach, describe, it } from "node:test";

let env;
before(async () => {
  env = await initializeTestEnvironment({
    projectId: "demo-rhagho",
    firestore: { rules: readFileSync(new URL("../firestore.rules", import.meta.url), "utf8") },
  });
});
after(async () => env.cleanup());
beforeEach(async () => env.clearFirestore());

const word = (uid, over = {}) => ({
  rhagho: "kaza", portuguese: "casa", partOfSpeech: "Substantivo", version: 1, sortOrder: 1,
  createdAt: Timestamp.now(), updatedAt: Timestamp.now(), createdBy: uid, updatedBy: uid,
  alternativeForms: [], synonyms: [], abbreviations: [], ...over,
});

describe("dictionary", () => {
  it("visitantes leem mas não escrevem", async () => {
    const guest = env.unauthenticatedContext().firestore();
    await assertSucceeds(getDoc(doc(guest, "dictionary/a")));
    await assertFails(setDoc(doc(guest, "dictionary/a"), word("x")));
    await assertFails(deleteDoc(doc(guest, "dictionary/a")));
  });
  it("usuário autenticado cria, altera e exclui", async () => {
    const db = env.authenticatedContext("u1").firestore();
    await assertSucceeds(setDoc(doc(db, "dictionary/a"), word("u1")));
    await assertSucceeds(updateDoc(doc(db, "dictionary/a"), { example: "ok", updatedBy: "u1", version: 2 }));
    await assertSucceeds(deleteDoc(doc(db, "dictionary/a")));
  });
  it("outro usuário pode editar, mas não forjar autoria nem trocar o criador", async () => {
    await env.withSecurityRulesDisabled(async (c) => setDoc(doc(c.firestore(), "dictionary/a"), word("u1")));
    const u2 = env.authenticatedContext("u2").firestore();
    await assertSucceeds(updateDoc(doc(u2, "dictionary/a"), { updatedBy: "u2" }));
    await assertFails(updateDoc(doc(u2, "dictionary/a"), { updatedBy: "u1" }));
    await assertFails(updateDoc(doc(u2, "dictionary/a"), { createdBy: "u2", updatedBy: "u2" }));
  });
  it("rejeita registro inválido", async () => {
    const db = env.authenticatedContext("u1").firestore();
    await assertFails(setDoc(doc(db, "dictionary/a"), word("u1", { rhagho: "" })));
    await assertFails(setDoc(doc(db, "dictionary/a"), word("u1", { version: "1" })));
  });
});

describe("meta e rateLimits", () => {
  it("meta: leitura pública, escrita só autenticada e só incrementando", async () => {
    const guest = env.unauthenticatedContext().firestore();
    const db = env.authenticatedContext("u1").firestore();
    await assertSucceeds(getDoc(doc(guest, "meta/dictionary")));
    await assertFails(setDoc(doc(guest, "meta/dictionary"), { revision: 1 }));
    await assertSucceeds(setDoc(doc(db, "meta/dictionary"), { revision: 1 }));
    await assertFails(setDoc(doc(db, "meta/dictionary"), { revision: 5 }));
    await assertSucceeds(setDoc(doc(db, "meta/dictionary"), { revision: 2 }));
  });
  it("rateLimits e coleções desconhecidas são inacessíveis ao cliente", async () => {
    const db = env.authenticatedContext("u1").firestore();
    await assertFails(getDoc(doc(db, "rateLimits/x")));
    await assertFails(setDoc(doc(db, "rateLimits/x"), { count: 0 }));
    await assertFails(setDoc(doc(db, "outra/x"), { a: 1 }));
  });
});
