using FluentValidation;
using YtPipeline.Core.Models;

namespace YtPipeline.Core.Validation;

/// <summary>
/// Guards against the most common Stage 1 failure: the model returning empty or
/// malformed JSON. If this passes, stages 2-5 can trust the document.
/// </summary>
public sealed class ScriptDocumentValidator : AbstractValidator<ScriptDocument>
{
    public ScriptDocumentValidator()
    {
        RuleFor(s => s.Title).NotEmpty().MaximumLength(100);
        RuleFor(s => s.Description).NotEmpty();
        RuleFor(s => s.Beats).NotEmpty().WithMessage("Script must have at least one beat.");

        RuleForEach(s => s.Beats).ChildRules(beat =>
        {
            beat.RuleFor(b => b.Narration).NotEmpty();
            beat.RuleFor(b => b.ImagePrompt).NotEmpty();
        });

        // Beat indices must be unique so images map cleanly onto beats.
        RuleFor(s => s.Beats)
            .Must(beats => beats.Select(b => b.Index).Distinct().Count() == beats.Count)
            .WithMessage("Beat indices must be unique.");
    }
}
