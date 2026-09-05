# LLM Confidence in the CTL Workflow

## Question

Does the reflection prompt imply that every LLM output naturally includes a calibrated confidence score? And if the score is not explicitly mentioned elsewhere, will the model generate it by default?

## Short answer

No. In this workflow, the confidence score is not a model-native property that appears automatically. It is a required output field because the application treats it as part of a business contract for routing, threshold checks, and human review.

## Where the requirement is stated

The requirement is explicitly written in the reflection prompt:

- [src/Cascade.CTL.Agent.Application/Prompts/OrchestratorPrompts.cs](../src/Cascade.CTL.Agent.Application/Prompts/OrchestratorPrompts.cs)
- In the `ReflectionSystemPrompt`, the system instructions say:
  - apply a confidence threshold policy
  - report `confidenceScore` as a continuous value in `[0.50, 0.99]`
  - include `confidenceScore` in the required JSON output schema

This is not a loose suggestion. The prompt mandates a structured response containing a numeric confidence value, and the system message explains how that number is meant to be used for decision making.

## Why this exists

This workflow is not just asking the model for a verdict; it is asking the model to produce a verdict that can be routed through deterministic business logic.

The score is used to decide:

- whether the verdict is accepted as-is,
- whether it should be forced to `NeedsHumanReview`,
- whether a result is considered clearly approved, conditional, or ambiguous.

That logic is enforced in the parser and workflow after the LLM responds:

- [src/Cascade.CTL.Agent.Application/Orchestration/VerdictParser.cs](../src/Cascade.CTL.Agent.Application/Orchestration/VerdictParser.cs)
- [src/Cascade.CTL.Agent.Application/Configuration/CTLAgentOptions.cs](../src/Cascade.CTL.Agent.Application/Configuration/CTLAgentOptions.cs)
- [src/Cascade.CTL.Agent.Domain/Models/CTLVerdictDto.cs](../src/Cascade.CTL.Agent.Domain/Models/CTLVerdictDto.cs)

The app reads `parsed.ConfidenceScore`, snaps it to discrete buckets, and remaps verdicts to human review when the value is too low. This means the confidence field is a decision-control input, not a model curiosity.

## Important distinction

There are two different ideas that are easy to confuse:

1. A model emitting a confidence-like score because the prompt asked for it.
2. A model having an intrinsic, calibrated probability estimate.

The repository follows option 1.

The model is being instructed to emit a score because the workflow requires a numeric gate. The app then treats that score as a heuristic business signal, not as a scientifically calibrated probability.

## Practical interpretation

If an application needs a yes/no gate, a threshold, or a human-review trigger, it should define a schema field such as `confidenceScore` and ask for it explicitly.

A model will not generally produce a reliable confidence value by default just because it is a large model. Without a prompt contract and a downstream parser, the field is usually absent, inconsistent, or methodologically weak.

## Recommended design principle

Use a clear separation between:

- verdict
- confidence-like score
- evidence trail
- policy-based threshold logic

This keeps the LLM as a candidate generator while the application remains the actual decision authority.

## Bottom line

The prompt is not saying “all LLM outputs have confidence.”
It is saying: “for this CTL workflow, the model must produce a confidence-like score because the application will use it for routing, threshold checks, and human review.”

That requirement is explicit in the prompt, enforced in the parser, and baked into the verdict DTO contract.
