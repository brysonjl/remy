namespace Remy.Speech;

/// <summary>Recognition failure carrying a user-facing message (pipeline.md 4.4/4.5, verbatim strings).</summary>
public sealed class SpeechRecognitionException : Exception
{
    public const string DidNotFinish = "Speech recognition did not finish. Your available preview is incomplete; use any available recovery recording to try again.";
    public const string ModelNotLoaded = "The speech model is not loaded. Prepare the model in Speech Model and try again.";
    public const string ModelLoadFailed = "The speech model could not be loaded. Use Speech Model to verify or download it again.";

    public SpeechRecognitionException(string userMessage, Exception? inner = null) : base(userMessage, inner) { }
}
