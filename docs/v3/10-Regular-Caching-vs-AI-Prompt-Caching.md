To understand why the response is still dynamic, it helps to contrast traditional web caching with AI prompt caching:

    Traditional Web Caching (Static): If you ask a server for ://google.com, it gives you the exact same static page it gave the last person. The server does zero new work.

    AI Prompt Caching (Dynamic): The LLM remembers the meaning and context of your long instructions (like your System Prompt or chat history), but it completely regenerates a brand-new response from scratch based on your new question.

What Actually Happens Inside the LLM

When an LLM processes text, it does it in two distinct phases:

    The Prefill Phase (Reading): The LLM reads your System Prompt, background data, and past chat. It does massive mathematical calculations to understand the context. This is slow and uses a lot of computing power.

    The Decoding Phase (Writing): The LLM uses that context to predict and generate the next response, word by word.

Prompt Caching only skips Phase 1.

The system looks at your System Prompt and says: "I already read this exact block of text 10 seconds ago. I saved my mathematical understanding of it in my RAM. I will load that understanding instantly."

Then, it hands that understanding over to the LLM. The LLM reads your new question, combines it with the cached context, and dynamically calculates a unique response.

An Analogy: The Open-Book Exam

Imagine you hire a human researcher to write custom reports for you based on a 500-page textbook.

    Without Prompt Caching: Every time you ask a new question, the researcher must re-read the entire 500-page textbook from page one, and then write the answer. This takes hours and costs a lot of money.

    With Prompt Caching: The researcher keeps the textbook open on their desk. When you ask a new question, they instantly look at the relevant page they already memorized and write a completely fresh, custom answer to your new question.

Because they didn't have to waste time re-reading the whole book, they charge you less money (the 50% to 90% discount) and give you the answer in seconds instead of minutes.