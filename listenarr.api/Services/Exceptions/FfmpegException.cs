public class FfmpegException : Exception
{
    public FfmpegException (string message, Exception? innerException = null) 
        : base(message, innerException)
    {}
}
