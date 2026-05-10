using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Host;

/// <summary>
/// Interactive Human-in-the-Loop service for CLI demos.
/// Pauses execution and prompts the operator via Console for a decision
/// before the agent finalizes the verdict.
/// </summary>
public sealed class InteractiveHumanReviewService : IHumanReviewService
{
    private readonly ILogger<InteractiveHumanReviewService> _logger;

    public InteractiveHumanReviewService(ILogger<InteractiveHumanReviewService> logger)
    {
        _logger = logger;
    }

    public Task<HumanReviewDecision> RequestReviewAsync(
        HumanReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[HITL] Human review requested — Asset: {AssetId}, Confidence: {Confidence:F2}, Session: {SessionId}",
            request.AssetId, request.ProposedVerdict.ConfidenceScore, request.SessionId);

        // Display the proposed verdict and evidence for the reviewer
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("  ╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("  ║  HUMAN REVIEW REQUIRED                                     ║");
        Console.WriteLine("  ╚══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  Asset:      {request.AssetId}");
        Console.WriteLine($"  Session:    {request.SessionId}");
        Console.WriteLine($"  Verdict:    {request.ProposedVerdict.Verdict}");
        Console.WriteLine($"  Confidence: {request.ProposedVerdict.ConfidenceScore:F2}");
        Console.ResetColor();
        Console.WriteLine();

        if (request.ProposedVerdict.EvidenceTrail.Length > 0)
        {
            Console.WriteLine("  EVIDENCE:");
            foreach (var evidence in request.ProposedVerdict.EvidenceTrail)
                Console.WriteLine($"    • {evidence}");
            Console.WriteLine();
        }

        if (request.ProposedVerdict.Conditions.Length > 0)
        {
            Console.WriteLine("  CONDITIONS:");
            foreach (var cond in request.ProposedVerdict.Conditions)
                Console.WriteLine($"    • {cond}");
            Console.WriteLine();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │  Choose an action:                                      │");
        Console.WriteLine("  │                                                         │");
        Console.WriteLine("  │  [1] Confirm — accept the NeedsHumanReview verdict      │");
        Console.WriteLine("  │  [2] Override → Clear                                   │");
        Console.WriteLine("  │  [3] Override → ClearWithConditions                     │");
        Console.WriteLine("  │  [4] Override → NotClear                                │");
        Console.WriteLine("  │                                                         │");
        Console.WriteLine("  └─────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.Write("  Your choice [1-4]: ");

        var choice = ReadChoice(cancellationToken);

        Console.Write("  Reviewer name (or press Enter for 'cli-reviewer'): ");
        var reviewer = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(reviewer))
            reviewer = "cli-reviewer";

        Console.Write("  Notes (or press Enter for default): ");
        var notes = Console.ReadLine()?.Trim();

        HumanReviewDecision decision;

        switch (choice)
        {
            case "2":
                decision = BuildOverride(CTLVerdict.Clear, 0.90, reviewer,
                    string.IsNullOrEmpty(notes)
                        ? "After manual review, all issues resolved — asset cleared for listing."
                        : notes);
                break;

            case "3":
                decision = BuildOverride(CTLVerdict.ClearWithConditions, 0.78, reviewer,
                    string.IsNullOrEmpty(notes)
                        ? $"Confidence {request.ProposedVerdict.ConfidenceScore:F2} is borderline. After manual review of evidence trail, asset cleared with conditions."
                        : notes);
                break;

            case "4":
                decision = BuildOverride(CTLVerdict.NotClear, 0.30, reviewer,
                    string.IsNullOrEmpty(notes)
                        ? "After manual review, asset has unresolvable issues — not cleared."
                        : notes);
                break;

            default: // "1" or anything else → Confirm
                decision = new HumanReviewDecision
                {
                    Action = HumanReviewAction.Confirm,
                    ReviewerNotes = string.IsNullOrEmpty(notes)
                        ? $"Confirmed NeedsHumanReview — confidence {request.ProposedVerdict.ConfidenceScore:F2} requires further investigation."
                        : notes,
                    ReviewedBy = reviewer
                };
                break;
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✓ Decision recorded: {decision.Action} by {decision.ReviewedBy}");
        Console.ResetColor();
        Console.WriteLine();

        _logger.LogInformation(
            "[HITL] Reviewer decision: {Action} — {Notes}",
            decision.Action, decision.ReviewerNotes);

        return Task.FromResult(decision);
    }

    private static HumanReviewDecision BuildOverride(CTLVerdict verdict, double confidence, string reviewer, string notes) =>
        new()
        {
            Action = HumanReviewAction.OverrideVerdict,
            OverriddenVerdict = verdict,
            OverriddenConfidence = confidence,
            ReviewerNotes = notes,
            ReviewedBy = reviewer
        };

    private static string ReadChoice(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = Console.ReadLine()?.Trim() ?? "1";
            if (line is "1" or "2" or "3" or "4")
                return line;
            Console.Write("  Invalid choice. Enter 1, 2, 3, or 4: ");
        }
    }
}
