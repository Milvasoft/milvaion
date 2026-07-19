namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// Arka planda çalışan servislerin ayarları.
/// </summary>
/// <remarks>
/// Bunlar appsettings içinde ayrı bölümler olarak duruyordu ama Configuration
/// ekranında hiç görünmüyordu. Bir işin neden dağıtılmadığını ya da logların
/// neden geç geldiğini araştıran biri, önce bu servislerin açık olup olmadığına
/// ve hangi aralıkla çalıştığına bakıyor.
/// </remarks>
public class BackgroundServicesConfigDto
{
    /// <summary> Worker ve iş tiplerinin otomatik keşfi. </summary>
    public ToggleConfigDto WorkerAutoDiscovery { get; set; }

    /// <summary> Takılı kalmış çalışmaları tespit eden servis. </summary>
    public ZombieDetectorConfigDto ZombieOccurrenceDetector { get; set; }

    /// <summary> Worker loglarını toplayıp veritabanına yazan servis. </summary>
    public BatchServiceConfigDto LogCollector { get; set; }

    /// <summary> Çalışma durumlarını toplu güncelleyen servis. </summary>
    public StatusTrackerConfigDto StatusTracker { get; set; }

    /// <summary> Dead letter kayıtlarını işleyen servis. </summary>
    public ToggleConfigDto FailedOccurrenceHandler { get; set; }

    /// <summary> Hangfire ve Quartz gibi dış zamanlayıcıları izleyen servis. </summary>
    public ExternalJobTrackerConfigDto ExternalJobTracker { get; set; }

    /// <summary> Workflow adımlarını yürüten servis. </summary>
    public PollingServiceConfigDto WorkflowEngine { get; set; }
}

/// <summary>
/// Yalnızca açık kapalı bilgisi taşıyan servisler için.
/// </summary>
public class ToggleConfigDto
{
    /// <summary> Servis çalışıyor mu. </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Belirli aralıkla yoklama yapan servisler için.
/// </summary>
public class PollingServiceConfigDto
{
    /// <summary> Servis çalışıyor mu. </summary>
    public bool Enabled { get; set; }

    /// <summary> İki yoklama arasındaki süre. </summary>
    public int PollingIntervalSeconds { get; set; }
}

/// <summary>
/// Kayıtları toplu işleyen servisler için.
/// </summary>
public class BatchServiceConfigDto
{
    /// <summary> Servis çalışıyor mu. </summary>
    public bool Enabled { get; set; }

    /// <summary> Bir seferde işlenen kayıt sayısı. </summary>
    public int BatchSize { get; set; }

    /// <summary> Toplu yazma aralığı. </summary>
    public int BatchIntervalMs { get; set; }
}

/// <summary>
/// Durum güncelleme servisi; toplu yazmanın yanında saklanan log sayısını da sınırlıyor.
/// </summary>
public class StatusTrackerConfigDto : BatchServiceConfigDto
{
    /// <summary> Bir çalışma için saklanan en fazla log satırı. </summary>
    public int ExecutionLogMaxCount { get; set; }
}

/// <summary>
/// Takılı kalan çalışmaları tespit eden servis.
/// </summary>
public class ZombieDetectorConfigDto
{
    /// <summary> Servis çalışıyor mu. </summary>
    public bool Enabled { get; set; }

    /// <summary> İki tarama arasındaki süre. </summary>
    public int CheckIntervalSeconds { get; set; }

    /// <summary> Bu süreden uzun süredir ilerlemeyen çalışma ölü kabul ediliyor. </summary>
    public int ZombieTimeoutMinutes { get; set; }
}

/// <summary>
/// Dış zamanlayıcı izleme servisi.
/// </summary>
public class ExternalJobTrackerConfigDto
{
    /// <summary> Servis çalışıyor mu. </summary>
    public bool Enabled { get; set; }

    /// <summary> Bir seferde işlenen kayıt bildirimi sayısı. </summary>
    public int RegistrationBatchSize { get; set; }

    /// <summary> Bir seferde işlenen çalışma bildirimi sayısı. </summary>
    public int OccurrenceBatchSize { get; set; }

    /// <summary> Toplu yazma aralığı. </summary>
    public int BatchIntervalMs { get; set; }
}
