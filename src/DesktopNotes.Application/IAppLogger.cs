namespace DesktopNotes.Application;

public enum AppLogLevel { Information, Warning, Error }

public interface IAppLogger
{
    void Write(AppLogLevel level, string eventName, string message, Exception? exception = null);
}
