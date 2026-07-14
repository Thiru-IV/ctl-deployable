# Comprehensive Guide: Advanced RAG Architectures

This reference document outlines the core structural frameworks for Retrieval-Augmented Generation (RAG) and the most effective engineering patterns to reduce LLM API token consumption.

---

## Part 1: Advanced RAG Architectures

RAG architectures range from simple linear lookups to complex, autonomous multi-agent systems designed to solve challenges like data ambiguity, hallucinations, and multi-source reasoning.

### Architecture Matrix Summary

| Architecture | Structural Complexity | Primary Failure Mode Addressed | Latency Overhead |
| :--- | :--- | :--- | :--- |
| **Simple (Naïve)** | Extremely Low | None (Base Case) | Minimal |
| **Conversational** | Low | Missing historical chat references | Very Low |
| **HyDE** | Medium | Misaligned user querying vocabularies | Medium (Adds 1 LLM Call) |
| **Corrective (CRAG)** | Medium-High | Database hallucinations / Out-of-bounds queries | High if fallback web-search triggers |
| **Self-RAG** | High | Factually ungrounded / Irrelevant text generations | High (Iterative looping token checks) |
| **GraphRAG** | High | Blind spots regarding relational context & dependencies | Medium-High |
| **Agentic** | Very High | Multistep problem solving & reasoning blockages | Variable (Depends on loop iterations) |

---

### Operational Workflows & Flowcharts

#### 1. Simple (Naïve) RAG
The baseline approach. It is entirely linear and synchronous, with no validation loops or decision-making stages.

```text
[ User Query ]
      │
      ▼
[ Vector Embedding ] ──(Similarity Search)──► [ Vector Database ]
      │                                              │
      ▼                                              ▼
[ Prompt Aggregator ] ◄────────(Top-K Chunks)────────┘
      │
      ▼
  [ LLM ] ──► [ Grounded Response ]
```
* **Operation:** The query converts straight into an embedding, grabs the closest static data chunks, bundles them directly into a prompt template, and leaves the final synthesis entirely up to the LLM.

---

#### 2. Conversational RAG
Conversational RAG flow happens well b4 LLM hit
Enhances the simple RAG architecture by adding memory, allowing the system to recall previous turns in a conversation and use that historical context to influence subsequent searches.

```text
[ User Query ] ──► [ Contextualizer / Query Rewriter ] ◄──► [ Memory Bank ]
                                  │
                                  ▼
                        [ Rewritten Query ]
                                  │
                                  ▼
                        [ Standard RAG Pipeline ] ──► [ LLM Response ]
```
* **Operation:** If a user says "What is its price?", the re-writer pulls context from the Memory Bank (e.g., "The user is talking about Product X") and rewrites the query to "What is the price of Product X?" before hitting the vector store.

**Vector Database is completely blind to your chat history**
If your application passes the raw user query "What is its price?" directly into an embedding model to search your Vector Database, the vector embedding will literally look for documents matching the words "What," "is," "its," and "price." Because the word "its" has no semantic meaning on its own, your vector search will return completely irrelevant text chunks, resulting in a failed RAG generation.

The Solution: Isolating the Search Step

---

#### 3. HyDE (Hypothetical Document Embeddings)
An architecture for ambiguous queries. The system first generates an ideal (but potentially fictional) answer to the user's query, embeds that text, and then searches for real documents that closely match the hypothetical text's structure.

```text
[ User Query ] ──► [ LLM (Zero-Shot) ] ──► [ Hypothetical Answer (Fake Doc) ]
                                                   │
                                                   ▼
[ Vector DB ] ◄──(Find matching structures)── [ Vector Embedding ]
      │
      ▼
[ Real Source Chunks ] ──► [ Final LLM ] ──► [ Validated Response ]
```
* **Operation:** Instead of embedding the user's *question*, it asks an LLM to generate a fake, ideal *answer*. It then uses the vector representation of that fake answer to search for real text chunks that mirror the same narrative structure.

---

#### 4. Corrective RAG (CRAG)
Focuses on validation. If the retrieved documents are irrelevant or low-quality, this architecture triggers a fallback mechanism—such as an automated web search—to correct and filter the data before generating a final response.

```text
  [ User Query ] ──► [ Vector DB ] ──► [ Evaluator / Grader ]
                                               │
               ┌───────────────────────────────┼──────────────────────────────┐
               ▼ (Correct / High Confidence)   ▼ (Ambiguous / Low Confidence) ▼ (Incorrect)
       [ Local Context Chunks ]        [ Hybrid Data Fusion ]         [ Trigger Web Search ]
               │                               │                              │
               └───────────────────────────────┼──────────────────────────────┘
                                               ▼
                                      [ Prompt Bundler ] ──► [ LLM Response ]
```
* **Operation:** An independent grading mechanism checks the relevance of retrieved chunks. If the data is garbage or missing, it blocks execution and routes out to an authorized Web Search API or fallback dataset to fetch correct data before generation.

When ONLY system triggers the fallback web search Hybrid Data Fusion step stitches the internal and external text blocks together, to patch the information gap before hitting the LLM. The system bypasses web search and data fusion entirely when the evaluator checks your internal database chunks (The Primary Route) and gives a perfect score

---

#### 5. Self-RAG 
Features an iterative feedback loop where the LLM critiques its own answers. If the output is deemed incorrect, unsupported, or incomplete, the model will autonomously initiate a new retrieval cycle.
If you are building an agent using top-tier frontier models, you should absolutely use the ReAct Function Calling pattern. It is cleaner and completely native.

```text
[ Query ] ──► [ Generator LLM ] ───► Is Retrieval Needed? (Generate [Is_Retrieve] Token)
                     ▲       │
                     │       ├─► YES ──► [ Run Vector Search ] ──► Eval Relevance
                     │       │                                          │
                     │       └─► NO  ──► Generate Content Chunks        ▼
                     │                                             Eval Supported?
                     │                                                  │
                     └───────◄─── NO (Loop back / Re-try) ◄─────────────┤
                                                                        ▼ YES
                                                               [ Output Final Answer ]
```
* **Operation:** The underlying LLM is specifically fine-tuned to emit special internal reflection tokens (like `[Retrieve]`, `[Critique]`, or `[Utility]`). It systematically grades whether its own text is fully supported by the text chunks.

---

#### 6. GraphRAG
Instead of relying entirely on raw text chunks, this architecture builds a knowledge graph. It retrieves context based on the underlying relationships, entities, and connections between data points.

```text
[ User Query ] ──► [ Global / Local Entity Tracker ]
                          │
                          ▼
                  [ Semantic Index ]
                          │
     ┌────────────────────┴────────────────────┐
     ▼ (Vector Entry Points)                   ▼ (Relational Mapping)
[ Vector Sub-Graph ] ──────────────────► [ Graph Database ]
                                               │
                                               ▼
                                   [ Graph Traversal Loop ]
                                   (Entity A ──► Relationship ──► Entity B)
                                               │
                                               ▼
                                   [ Community Summaries ] ──► [ LLM Response ]
```
* **Operation:** The system executes a classic vector search to find initial entry points, but then shifts into a Graph Traversal routine, walking the literal connections ("Entity A depends on Component B") to pull highly contextual community summaries.

---

#### 7. Agentic RAG
An autonomous architecture that acts like a researcher. It breaks tasks into smaller steps, plans its own retrieval process, and uses external tools (like web search or APIs) to verify information.

```text
                    ┌───────────────── [ User Query ] ─────────────────┐
                    │                                                  │
                    ▼                                                  ▼
           [ LLM Orchestrator ] ◄──────────────────────────────┐ [ Memory State ]
                    │                                          │
    ┌───────────────┼───────────────┬────────────────┐         │
    ▼               ▼               ▼                ▼         │
[Tool 1: DB]   [Tool 2: Web]   [Tool 3: Vector]  [Tool 4: Code]│
    │               │               │                │         │
    └───────────────┴───────────────┼────────────────┘         │
                                    ▼                          │
                        [ Evaluate Tool Results ] ─────────────┘
                                    │ (If task criteria satisfied)
                                    ▼
                          [ Final Synthesized Answer ]
```
* **Operation:** The agent evaluates the query and acts as a dynamic router. It can choose to query a vector index, execute a mathematical script via an execution playground, review the outcome, and loop back iteratively until it has resolved the entire request.

---