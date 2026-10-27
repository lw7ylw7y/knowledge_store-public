# Retrieval evaluation, 2026-10-05 20:32

- Embedder: none (keyword search only)
- Library: 11 books, 1595 chunks
- Coverage threshold used: 0.45
- Questions: 32 answerable, 8 out of library

> No vector search ran (keyword only), so the similarity and threshold results below are not meaningful.

## Summary

| Metric | Result | Target |
| --- | --- | --- |
| recall@5 | 69% (22/32) | 80% or more |
| recall@8 | 72% | |
| MRR | 0.59 | |
| not-covered accuracy | 25% (2/8) | 80% or more |
| answerable wrongly refused | 3% (1/32) | as low as possible |
| search time p95 | 9 ms | 10000 ms or less |

**Result: a target is missed.**

## By question type

| Type | Questions | recall@5 | MRR |
| --- | --- | --- | --- |
| answerable | 24 | 79% | 0.66 |
| cross-language | 3 | 0% | 0.00 |
| keyword | 2 | 100% | 1.00 |
| paraphrase | 3 | 33% | 0.33 |

| Language | Questions | recall@5 |
| --- | --- | --- |
| ar | 1 | 100% |
| en | 23 | 65% |
| es | 6 | 83% |
| ja | 2 | 50% |

## Questions

| Id | Type | Rank of correct hit | Best similarity | Refused | Top hit |
| --- | --- | --- | --- | --- | --- |
| alice-01 | answerable | 8 | n/a | no | Alice's Adventures in Wonderland / CHAPTER I. Down the Rabbit-Hole |
| alice-02 | answerable | 2 | n/a | no | Alice's Adventures in Wonderland / CHAPTER I. Down the Rabbit-Hole |
| alice-03 | answerable | 1 | n/a | no | Alice's Adventures in Wonderland / CHAPTER V. Advice from a Caterpillar |
| alice-04 | answerable | 5 | n/a | no | Alice's Adventures in Wonderland / CHAPTER VI. Pig and Pepper |
| alice-05 | answerable | 1 | n/a | no | Alice's Adventures in Wonderland / CHAPTER VIII. The Queen’s Croquet-Ground |
| alice-06 | answerable | 4 | n/a | no | Alice's Adventures in Wonderland / Contents |
| alice-07 | answerable | 3 | n/a | no | Alice's Adventures in Wonderland / Contents |
| alice-08 | paraphrase | miss | n/a | no | Pride and Prejudice / CHAPTER XX. |
| alice-09 | keyword | 1 | n/a | no | Alice's Adventures in Wonderland / CHAPTER VIII. The Queen’s Croquet-Ground |
| pride-01 | answerable | miss | n/a | no | Pride and Prejudice / CHAPTER VI. |
| pride-02 | answerable | 1 | n/a | no | Pride and Prejudice / Chapter I. |
| pride-03 | answerable | 1 | n/a | no | Pride and Prejudice / “to assure you in the most animated language.” CHAPTER XIX. |
| pride-04 | answerable | miss | n/a | no | Pride and Prejudice / “With an affectionate smile.” CHAPTER LI. |
| pride-05 | answerable | miss | n/a | no | Pride and Prejudice / Piling up the fire. CHAPTER XI. |
| pride-06 | paraphrase | miss | n/a | no | Pride and Prejudice / “With an affectionate smile.” CHAPTER LI. |
| pride-07 | cross-language | miss | n/a | no | Don Quijote / Capítulo III. Del ridículo razonamiento que pasó entre don Quijote, Sancho Panza y el bachiller Sansón Carrasco |
| pride-08 | keyword | 1 | n/a | no | Pride and Prejudice / CHAPTER IV. |
| quijote-01 | answerable | 1 | n/a | no | Don Quijote / Capítulo VIII. Del buen suceso que el valeroso don Quijote tuvo en la espantable y jamás imaginada aventura de los molinos de viento, con otros sucesos dignos de felice recordación |
| quijote-02 | answerable | 1 | n/a | no | Don Quijote / Capítulo IV. Donde Sancho Panza satisface al bachiller Sansón Carrasco de sus dudas y preguntas, con otros sucesos dignos de saberse y de contarse |
| quijote-03 | answerable | 1 | n/a | no | Don Quijote / Capítulo V. Donde se prosigue la narración de la desgracia de nuestro caballero |
| quijote-04 | cross-language | miss | n/a | no | Pride and Prejudice / “The first pleasing earnest of their welcome.” CHAPTER XLVII. |
| quijote-05 | answerable | 1 | n/a | no | Don Quijote / Capítulo XXVI. Donde se prosiguen las finezas que de enamorado hizo don Quijote en Sierra Morena |
| quijote-06 | answerable | 1 | n/a | no | Don Quijote / Capítulo XXI. Que trata de la alta aventura y rica ganancia del yelmo de Mambrino, con otras cosas sucedidas a nuestro invencible caballero |
| kokoro-01 | answerable | miss | n/a | yes | none |
| kokoro-02 | answerable | 1 | n/a | no | こころ / 中 両親と私 |
| kokoro-03 | cross-language | miss | n/a | no | Pride and Prejudice / “Hearing herself called.” CHAPTER XXXV. |
| origin-01 | answerable | 1 | n/a | no | On the Origin of Species /  |
| origin-02 | answerable | 3 | n/a | no | Two Column Paper /  |
| origin-03 | paraphrase | 1 | n/a | no | On the Origin of Species /  |
| notes-01 | answerable | 1 | n/a | no | Reading Notes / Habits |
| notes-02 | answerable | 1 | n/a | no | Reading Notes / Memory |
| arabic-01 | answerable | 1 | n/a | no | arabic / arabic |
| ool-01 | out-of-library | n/a | n/a | yes | none |
| ool-02 | out-of-library | n/a | n/a | no | Don Quijote / Capítulo XLVII. Del estraño modo con que fue encantado don Quijote de la Mancha, con otros famosos sucesos |
| ool-03 | out-of-library | n/a | n/a | no | Pride and Prejudice / CHAPTER LXI. |
| ool-04 | out-of-library | n/a | n/a | no | On the Origin of Species /  |
| ool-05 | out-of-library | n/a | n/a | no | Pride and Prejudice / A note for Miss Bennet. CHAPTER VII. |
| ool-06 | out-of-library | n/a | n/a | yes | none |
| ool-07 | out-of-library | n/a | n/a | no | Don Quijote / Capítulo XXXII. De la respuesta que dio don Quijote a su reprehensor, con otros graves y graciosos sucesos |
| ool-08 | out-of-library | n/a | n/a | no | Alice's Adventures in Wonderland / THE FULL PROJECT GUTENBERG™ LICENSE |

## Misses (correct passage not in the top 5)

- alice-01: How does Alice fall down the rabbit-hole and what does she notice on the way down?
- alice-08: A girl chases a hurrying animal into a burrow and tumbles for a very long time
- pride-01: Why does Mr. Darcy refuse to dance with Elizabeth at the ball?
- pride-04: What scandal does Lydia cause by running away?
- pride-05: How does Darcy first declare his feelings to Elizabeth?
- pride-06: Why is the mother so anxious to see her daughters wed?
- pride-07: ¿Cómo le declara el señor Darcy su amor a Elizabeth?
- quijote-04: Why does Don Quixote believe the windmills are giants?
- kokoro-01: 「先生」とは誰のことですか
- kokoro-03: Why does the narrator avoid giving the teacher's real name?

## Coverage mistakes

- Wrongly refused: kokoro-01: 「先生」とは誰のことですか
- Wrongly answered: ool-02: How do I change a flat tire on a bicycle?
- Wrongly answered: ool-03: What is the capital city of Australia?
- Wrongly answered: ool-04: Explain how photosynthesis produces glucose in leaves
- Wrongly answered: ool-05: What are the common symptoms of influenza?
- Wrongly answered: ool-07: ¿Cómo se cocina una paella valenciana?
- Wrongly answered: ool-08: Which programming language is best for building web servers?
