using Remy.Core.Transforms;

namespace Remy.Core.Settings;

/// <summary>The "textTransforms" key (v0.2-features.md §8.2). Mutate through <see cref="TransformStore"/>.</summary>
public sealed partial class RemySettings
{
    private IReadOnlyList<TextTransform> _textTransforms = TransformStore.Normalize([]);

    /// <summary>Normalized list: Polish, Prompt Engineer, then up to 8 custom transforms. Setting normalizes.</summary>
    public IReadOnlyList<TextTransform> TextTransforms
    {
        get => _textTransforms;
        set
        {
            var v = TransformStore.Normalize(value);
            if (_textTransforms.SequenceEqual(v)) return;
            _textTransforms = v;
            Raise(nameof(TextTransforms));
        }
    }

    partial void AddTransformEntries(EntryBuilder b) => b.Raw("textTransforms",
        n => TextTransforms = TransformStore.FromJson(n),
        () => TransformStore.ToJson(TextTransforms));
}
