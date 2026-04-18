public class DownloadProcessingException : Exception
{
    public DownloadProcessingException (string message, Exception? innerException = null) 
        : base(message, innerException)
    {}
}
