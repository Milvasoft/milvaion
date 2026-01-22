namespace Milvaion.Application.Utils.Constants;

/// <summary>
/// Represents a class that contains global constants for the application.
/// </summary>
public static class CacheConstant
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
{
    public static class Time
    {
        public const int Seconds10 = 10;
        public const int Seconds30 = 30;
        public const int Seconds60 = 60;
        public const int Seconds120 = 120;
        public const int Seconds300 = 300;
        public const int Seconds600 = 600;
    }
    public static class Key
    {
        public const string Configurations = "configs";
        public const string DatabaseStats = "db_stats";
    }
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
