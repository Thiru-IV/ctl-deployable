# AI Agent Patterns: Why Every Source Has a Different List

## Short Answer

There is **no official, universally accepted list of AI agent patterns.**

The confusion comes from the fact that different people use the word **"pattern"** to describe different levels of an AI system. Two articles may both be correct while discussing entirely different aspects of agent design.

---

# Why Every Article Has a Different List

Think about web development.

One person might say design patterns are:

- MVC
- Repository
- Observer

Another might say:

- Load Balancer
- CDN
- Cache Aside

Another might say:

- Retry
- Circuit Breaker

All of them are correct.

They're simply talking about different architectural layers.

The same thing happens with Agentic AI.

---

# Five Levels of AI Agent Patterns

## 1. Reasoning Patterns

These describe **how the model thinks**.

Examples:

- ReAct
- Reflection
- Self-Critique
- Chain of Thought (CoT)
- Tree of Thoughts (ToT)
- Debate
- Least-to-Most Prompting

These are often called **reasoning or cognitive patterns**.

---

## 2. Workflow Patterns

These describe **how tasks move through a system**.

Examples:

- Prompt Chaining
- Router / Classifier
- Fan-out
- Fan-in
- Parallel Execution
- Planner
- Executor
- Tournament
- Voting
- Reviewer
- Retry
- Human Approval

These are orchestration or workflow patterns.

---

## 3. Agent Architecture Patterns

These describe **how one or more agents collaborate**.

Examples:

- Single Agent
- Planner + Executor
- Manager-Worker
- Multi-Agent
- Hierarchical Agents
- Swarm
- Blackboard Architecture
- Specialist Agents

These are architectural patterns.

---

## 4. Infrastructure Patterns

These are software engineering patterns that support agents.

Examples:

- Tool Calling
- MCP (Model Context Protocol)
- Memory
- RAG
- Event-Driven Systems
- Queue-Based Processing
- Pub/Sub
- Async Workers

These are implementation and infrastructure patterns.

---

## 5. Safety & Reliability Patterns

These ensure trustworthy outputs.

Examples:

- Adversarial Check
- Constitutional Review
- Guardrails
- Policy Checker
- Output Validation
- Sandbox Execution
- Permission Gates
- Human-in-the-Loop

These focus on governance and reliability.

---

# Why Different Organizations Show Different Patterns

## LangChain / LangGraph

LangGraph primarily focuses on **workflow orchestration**.

Typical patterns include:

- Router
- Planner
- Parallelization
- Reflection
- Evaluator
- Tool Use
- Orchestrator

Their goal is to teach application workflows rather than define all possible AI agent patterns.

---

## Anthropic

Anthropic's workflow documentation focuses on five major workflows:

- Prompt Chaining
- Routing
- Parallelization
- Orchestrator-Workers
- Evaluator-Optimizer

These are workflow patterns, not a complete taxonomy of Agentic AI.

---

## OpenAI

OpenAI documentation commonly discusses:

- Agents
- Tool Calling
- Planning
- Memory
- Handoffs
- Human-in-the-Loop
- Responses API

These are implementation concepts rather than an official classification of patterns.

---

## Microsoft

Microsoft's AI architecture guidance commonly includes:

- Sequential Workflows
- Concurrent Workflows
- Reflection
- Planning
- Routing
- Debate
- Multi-Agent Systems

Their focus is enterprise architecture.

---

## Google

Google's AI documentation discusses topics such as:

- Reasoning
- Planning
- Tool Use
- Multi-Agent Systems
- Orchestration

Again, these are design recommendations rather than an official standard.

---

# Is There an Official Standard?

**No.**

Currently there is **no official standard** from:

- IEEE
- ISO
- OpenAI
- Anthropic
- Google
- Microsoft
- LangChain

that defines **the official list of AI agent patterns**.

The field is evolving rapidly, and every organization categorizes patterns according to its own framework.

---

# Which Sources Are Considered Most Reliable?

If you're studying Agentic AI today, the following are among the most influential sources:

1. Anthropic
   - Workflow patterns
   - Prompt Chaining
   - Routing
   - Parallelization
   - Orchestrator-Workers
   - Evaluator-Optimizer

2. OpenAI
   - Agents
   - Tool Calling
   - Planning
   - Memory
   - Human-in-the-Loop

3. LangChain / LangGraph
   - Workflow orchestration
   - Agent graphs
   - Multi-agent workflows

4. Microsoft
   - Enterprise AI architecture
   - Multi-agent systems
   - Orchestration

5. Google
   - Agent design
   - Reasoning
   - Planning
   - Orchestration

---

# A Practical Taxonomy

Instead of memorizing different lists from different blogs, organize AI agent patterns into four broad categories.

| Category | Typical Patterns |
|-----------|------------------|
| **Reasoning** | ReAct, Reflection, Self-Critique, Debate, Tree of Thoughts |
| **Workflow** | Prompt Chaining, Routing, Classifier, Fan-out, Fan-in, Planner, Tournament, Voting, Evaluator, Retry |
| **Architecture** | Single Agent, Planner-Executor, Manager-Worker, Multi-Agent, Hierarchical Agents, Swarm |
| **Safety & Reliability** | Guardrails, Adversarial Check, Output Validation, Human Approval, Policy Enforcement |

---

# Key Takeaway

Different sources are usually **not contradicting each other**.

Instead, they are describing patterns at **different abstraction layers**:

- **Reasoning Patterns** → How an LLM thinks.
- **Workflow Patterns** → How work flows through a process.
- **Architecture Patterns** → How agents are organized.
- **Infrastructure Patterns** → The technical foundation.
- **Safety Patterns** → How outputs are validated and governed.

Understanding these layers makes it much easier to reconcile the different lists you'll encounter across blogs, documentation, conference talks, and open-source projects.