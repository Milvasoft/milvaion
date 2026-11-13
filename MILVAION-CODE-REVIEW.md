# Milvaion – Kod ve Mimari İnceleme Raporu

**Tarih:** 2026-09-23
**Kapsam:** `C:\Users\bugra\source\repos\github\Milvaion` — tüm kaynak (~90k satır C#, ~8k satır React, 14 CI workflow, Docker/compose, testler). `bin/obj/node_modules/.vs/.git` hariç 1.405 dosya okundu.
**Yöntem:** Beş alan (Application+Domain, Infrastructure, API+güvenlik, SDK+Workers, UI+DevOps+Testler) ayrı ayrı satır satır incelendi; kritik iddialar ikinci bir turda kodda tekrar doğrulandı. Her bulgu `dosya:satır` ile verilmiştir. Emin olunmayan noktalar "güven: orta/düşük" olarak işaretlendi.

---

## 1. Yönetici Özeti

Milvaion iyi organize edilmiş, okunabilir, feature-folder düzenli ve gerçek servislerle (Postgres/Redis/RabbitMQ) test edilen bir kod tabanı. Controller'lar ince, MCP tool'ları permission guard'lı, Dockerfile'lar non-root, UI'da XSS sink'i yok, 1.671 test metodunun hepsinde assertion var. Ancak **"distributed job scheduler" olma iddiasının kalbinde — dispatcher, durum takibi ve worker outbox'ında — çoklu instance ve hata anında veri kaybı/çift çalıştırma üreten tasarım açıkları var**, ve **prod/dev ayrımı kırık olduğu için default imaj anonim SuperAdmin erişimi veriyor.**

**Toplam bulgu:** ~13 Critical, ~40 High, ~80 Medium, ~65 Low (alan raporlarında ayrıntılı).

### En acil 10 madde (bugün düzeltilmeli)

| # | Sev | Bulgu | Konum |
|---|-----|-------|-------|
| 1 | 🔴 | **Anonim SuperAdmin JWT Scalar UI'a gömülü.** `GenerateTokenForUI` gerçek imza anahtarıyla 1 yıl geçerli `App.SuperAdmin` token üretip `/api/documentation` sayfasına yazıyor. Guard `MILVA_ENV=="prod"`; Dockerfile set etmiyor, compose `dev` → varsayılan kurulumda **herkes SuperAdmin**. | `Api/AppStartup/ApplicationBuilderExtensions.cs:27-64` |
| 2 | 🔴 | **`DeveloperController` auth'suz.** `database/reset` (DB drop), `database/seed`, `database/init` (root şifresini döndürüyor) anonim çağrılabilir; sadece `reset/ui` `[Auth]` taşıyor. | `Api/Controllers/DeveloperController.cs:23-66` |
| 3 | 🔴 | **Tüm secret'lar repoda hardcoded** ve `ValidateIssuer/Audience=false`: JWT `SymmetricPublicKey`, `ApiKey.Secret`, Postgres şifresi (`N4SQp.qW>6?xwWzg` — appsettings + compose + 3 worker appsettings), `MILVAION_ROOT_PASSWORD=admin`, LDAP BindPassword. Anahtarı değiştirmeyen her kurulum için dışarıdan SuperAdmin JWT üretilebilir. | `Api/appsettings.json`, `docker-compose.yml`, `Workers/*/appsettings.json` |
| 4 | 🔴 | **Dispatcher'da leader election / atomik claim yok.** Docs `locks:dispatcher` iddia ediyor ama sadece `startup_recovery` lock'u var. `GetDueJobsAsync` non-destructive ZRANGEBYSCORE; instance B, A lock'u bıraktıktan sonra aynı işi tekrar kilitleyip **aynı işi ikinci kez dispatch ediyor** (lock altında score re-check yok). | `Infra/BackgroundServices/JobDispatcherService.cs:441-460, 887-934` |
| 5 | 🔴 | **Dispatch-retry ölü kod.** Publish hatası `Status=Failed` yazıyor ama retry sorgusu `Status==Queued` filtreliyor → one-time işler kayboluyor, cron işler her poll'da yeni Failed occurrence üretiyor. | `JobDispatcherService.cs:72-81` vs `:643` |
| 6 | 🔴 | **`JobOccurrence`'ta concurrency token yok** (RowVersion/xmin: 0 hit). StatusTracker / ZombieDetector / WorkflowEngine "RowVersion check" yorumlu read→BulkUpdate yapıyor; `DbUpdateConcurrencyException` handler'ları erişilemez → **lost update** (Completed → Running/Unknown'a ezilebilir). | `StatusTrackerService.cs`, `ZombieOccurrenceDetectorService.cs`, `WorkflowEngineService.cs` |
| 7 | 🔴 | **Worker offline outbox kırık.** `PublishFinalStatusAndFinalizeAsync` status'ü lokal store'a yazıp hemen `FinalizeJobAsync` çağırıyor; `GetNotSyncedStatusUpdates` finalize edilmişleri **dışlıyor** → RabbitMQ kesikken üretilen final Completed/Failed ve loglar asla sync edilmiyor, retention'da siliniyor; API tarafında occurrence Running kalıp zombie olarak Failed'a düşüyor. | `Sdk.Worker/.../LocalStateStore.cs:783-821`, `JobConsumer.cs:1065-1077` |
| 8 | 🔴 | **Occurrence silmek işi zamanlayıcıdan düşürüyor.** `DeleteJobOccurrence` `RemoveFromScheduledSetBulkAsync(jobIds)` çağırıyor; ZSET üyesi JobId → eski çalıştırmayı silen kullanıcı aktif cron işini API restart'a kadar durduruyor. | `Application/Features/ScheduledJobs/DeleteJobOccurrence/…Handler.cs:36-37` |
| 9 | 🔴 | **`JobVersions` geometrik büyüme.** `JobVersions.Add(JsonSerializer.Serialize(existingJob))` — `JobVersions` `[JsonIgnore]` değil; her snapshot önceki tüm snapshot'ları içeriyor → jsonb her güncellemede ~2× büyüyor. | `UpdateScheduledJobCommandHandler.cs:95`, `Sdk/Domain/ScheduledJob.cs:146` |
| 10 | 🔴 | **NuGet publish PR tetikleyicisiyle çalışıyor** (`pull_request` → `dotnet nuget push`), testsiz. Fork PR'ı secret'a erişemez ama iç branch PR'ı review'dan önce paket yayınlar. | `.github/workflows/sdk-nuget-publish.yml:9`, `worker-template-nuget-publish.yml:9` |

### Ardından (bu sprint)

- **Privilege escalation:** `App.SuperAdmin` sıradan bir Permission satırı; `RoleManagement.Update` / `UserManagement.Update` / `ApiKeyManagement.Create` yetkili herkes kendine SuperAdmin verebilir. ExternalIdentityService IdP grup adını yerel role isimle eşliyor (IdP'de "SuperAdmin" grubu → yerel SuperAdmin). LDAP filtresinde username kaçışsız (injection).
- **`JobsHub` auth'suz**, `MapHub` `RequireAuthorization` yok, olaylar (exception metni dahil) `Clients.All`'a; UI `accessTokenFactory` göndermiyor.
- **API key'ler her `[Auth]` endpoint'te geçerli** (default policy ApiKey şemasını içeriyor; yorum aksini iddia ediyor).
- **NRE:** `UpdateScheduledJobCommandHandler.cs:38` `if (existingJob == null) return Error(existingJob.Id, …)`.
- **`GetSystemConfiguration` Redis connection string'i API yanıtında** döndürüyor.
- **Startup recovery** her API pod restart'ında cluster genelindeki 2 dk'dan eski tüm Queued/Running occurrence'ları "System restart detected" ile Failed'a çekiyor (worker'lar ayrı process olmasına rağmen).
- **Emergency stop** in-memory bool → sadece lokal instance'ı durduruyor. **Consumer capacity** hiç uygulanmıyor (okunan key ile yazılan key farklı).
- **RabbitMQ consumer'lar kanal ölümünden toparlanmıyor**; `WorkerAutoDiscovery`/`FailedOccurrenceHandler` `ExecuteAsync`'ten throw → host ölüyor; health check yine "healthy". `RabbitMQPublisher` her publish'te yeni kanal + koşulsuz 500 ms bekleme → 100 işlik batch ≥12,5 s.
- **SDK:** `OfflineResilience.Enabled=false` iken worker hiçbir status/log raporlamıyor; publisher'lar kanal kapanınca kalıcı bozuluyor (AutomaticRecovery kapalı, paylaşılan IChannel kilitsiz, `Persistent=false`); graceful shutdown sırası ters (kanal kapanıyor → sonra iptal → ACK atılamıyor → restart'ta idempotency ile atlanıp kalıcı Cancelled); sync `IJobWithResult<T>` `MethodInfo.Invoke` ile `TargetInvocationException` sarmalıyor (PermanentJobException/OCE yakalanmıyor).
- **HttpWorker SSRF** (URL/proxy/redirect/IgnoreSslErrors/thumbprint job datasından, allow-list yok); **SqlWorker** korkuluksuz ve örnek config Milvaion'ın kendi DB'sine `postgres` süper kullanıcısıyla bağlı.
- **`OccurrenceArchiveJob` çalışamaz** (var olmayan `ScheduledTime` kolonu, `Level integer` vs string, arşiv+delete ayrı statement).
- **API Docker build kırık:** `COPY *.sln .` ama repoda sadece `Milvaion.slnx` var. **`build/scripts/*` kırık** (`ROOT_DIR` bir seviye yanlış). API HEALTHCHECK `/api/health` → SPA fallback yüzünden hep geçiyor.
- **docker-compose:** 5432/5672/15672/5341/9090/3000 host'a açık, auth'suz pgweb (8082) tam DB erişimi, Grafana admin/admin, Seq ChangeMe123!, Prometheus `--web.enable-lifecycle`.
- **UI:** access+refresh token `localStorage`'da; OIDC silent renew dinleyicisi yok, 419 akışı OIDC refresh token'ını `/account/login/refresh`'e gönderiyor → SSO kullanıcıları login'e düşüyor; workbox `/api/` yanıtlarını Cache Storage'a alıyor; ~2.3k satır ölü sayfa.
- **Testler:** yetki (403) hiç test edilmiyor (tüm integration testleri SuperAdmin); API key / LDAP / OIDC / MCP guard testi yok; 257 `Task.Delay`, 2 skip ("CI flaky"), servis sızıntısı (`StopAsync` finally'de değil); Docker/NuGet publish test sonucuna bağlı değil.

---

## 2. Mimari Değerlendirme

### 2.1 İyi olanlar
- Feature-folder CQRS düzeni tutarlı; yeni yazılmış bölümler (`GetUpcomingExecutionList`, `GetJobOccurrenceLog*`, `WorkflowStepExtensions`, `UpdateWorkflow`) sınırlandırılmış sorgular ve açıklayıcı yorumlarla iyi tasarlanmış.
- Controller'lar ince, response envelope tutarlı; MCP tool'larının tamamı `McpPermissionGuard` arkasında.
- Testcontainers ile gerçek Redis/RabbitMQ/Postgres üzerinde dispatcher / status tracker / workflow engine / SDK ack-nack-DLQ senaryoları kapsanmış; `CronProjectorTests` mevcut.
- Dockerfile'lar multi-stage, non-root, SBOM/provenance üretiyor.
- SDK ↔ API mesaj sözleşmeleri (queue adları, alan adları, enum, iptal mesajı) uyumlu; template projeleri güncel SDK ile uyumlu.

### 2.2 Yapısal sorunlar

1. **Onion iddiası kâğıt üstünde.** ARCHITECTURE.md "Domain bağımsız, Application sadece Domain'e bağımlı" diyor; gerçekte Domain ASP.NET Core + EF Core'a, Application 14 dosyada EF Core'a, Npgsql `ILike`'a, `HttpContext`'e ve `IConfiguration`'a bağımlı; arayüzler teknoloji adlı (`IRedis*`, `IRabbitMQPublisher`). Asıl domain (`ScheduledJob`, `JobOccurrence`, `Workflow*`) Domain projesinde değil **SDK'da** — yani wire format = EF entity (audit alanları job'lara sızıyor, sürüm alanı yok, breaking change riski). Öneri: ya dokümanı gerçeğe çekin ya da (tercihen) SDK'da ayrı wire-DTO'lar tanımlayıp Domain'i SDK'dan koparın; port arayüzlerini teknoloji-adsız yapın (`IScheduleStore`, `IJobQueue`).

2. **Çoklu instance için tasarlanmamış singleton background servisler.** Dispatcher, WorkflowEngine, ZombieDetector, MisfireMonitor, EmergencyStop hepsi "tek instance" varsayıyor; docs ise horizontal scaling vaat ediyor. İki seçenek: (a) açık leader election (Redis `SET NX PX` + fencing token, yalnızca lider dispatch eder) — en ucuz; (b) her servisi multi-writer-safe yapmak: ZSET claim'i tek Lua script'te (`ZRANGEBYSCORE` + `ZADD`/`ZREM`) atomik yapmak, occurrence state transition'larını `xmin` concurrency token + koşullu `ExecuteUpdate(... WHERE Status = @expected)` ile yapmak. Hangisi seçilirse dokümanı ona göre güncelleyin.

3. **DB ↔ Redis ↔ RabbitMQ arasında tutarlılık garantisi yok.** Persist → publish → ZSET update zinciri herhangi bir adımda kopabiliyor; circuit-breaker fallback'leri sessizce `false` dönüyor ve dispatcher dönüş değerini yoksayıyor. Öneri: occurrence'ı `DispatchPending` ile persist et → publish → `Queued`'a geçir (outbox pattern); Redis'i "derived state" say ve DB'den periyodik reconciliation yap (bugün sadece startup'ta yapılıyor — C2/Delete bulgusunun kökü de bu).

4. **Status akışında idempotency ve sıralama yok.** Out-of-order (Running after Completed), duplicate delivery, poison message, at-most-once ACK (LogCollector/ExternalJobTracker persist'ten önce ACK'lıyor) — hepsi mevcut. Öneri: status transition tablosu (`CanTransition(from, to)`), occurrence bazlı `LastStatusSequence`, ortak "batching consumer" base sınıfı (reconnect, DLX, prefetch config, ACK-after-persist).

5. **Prod/dev ayrımı tek bir env var'a bağlı ve varsayılan "dev".** Scalar token, DeveloperController, FakeDataGenerator, `Include Error Detail=true`, Seq'e tüm header'lar. Öneri: tehlikeli her şeyi `#if DEBUG` veya explicit opt-in flag (`Milvaion:Developer:Enabled=true`) arkasına alın, varsayılan kapalı; secret'ları `Options` validation ile zorunlu kılın (`ValidateOnStart`), boş/varsayılan değerde başlatmayı reddedin.

6. **Yetki modeli düz.** `SuperAdmin` bir Permission satırı, delegasyon kuralı yok. Öneri: "verilebilir izinler ⊆ çağıranın izinleri" kuralı tek bir yerde (PermissionManager) uygulanmalı; SuperAdmin atanabilir olmamalı; IdP rol eşlemesi explicit mapping tablosuyla yapılmalı.

7. **Mega sınıflar.** `JobDispatcherService` ~1.5k satır, `JobConsumer` benzeri. Poll / validate / claim / persist / publish / schedule-advance ayrı sınıflara bölünmeli — bulguların çoğu bu karmaşıklıkta gizlenmiş.

8. **Ölü/kalıntı kod.** `Dtos/AccountDtos/InternalNotifications/**` (Features ile birebir kopya 9 dosya), boş `YargitayDtos`, boş `GetJobOccurenceListWithCursor`, `Milvaion - Backup.Application.csproj`, boş `Milvaion.Api.SqlServer` (kod jsonb/ILike ile Postgres-only — SQL Server desteği iddiası gerçek değil), `ApiAuthAttribute`, 20 yıllık `GenerateToken`, `CancellationListener`, ölü SDK option'ları, UI'da ~2.3k satır ölü sayfa, 10 kopya Reporter job'u, 8 birebir kopya worker workflow'u, `.sh/.ps1/.bat` üçlü script kopyaları.

9. **Zaman/cron modeli.** Yalnızca UTC, misfire policy yok, Skip policy "bitince hemen çalıştır" anlamına geliyor, lockout `DateTimeOffset.Now` vs `.DateTime` karışımı. Öneri: job başına timezone, explicit misfire policy (FireNow / Skip / DoNothing), tüm zaman alanları `DateTimeOffset`/UTC.

### 2.3 Önerilen yol haritası

| Faz | İçerik |
|-----|--------|
| **0 – Bugün** | Scalar token + DeveloperController + secret'lar (madde 1-3); NuGet PR trigger (10); Dockerfile `*.sln` düzelt; JobsHub `[Authorize]`; DeleteJobOccurrence satırını sil; NRE; JobVersions `[JsonIgnore]`. |
| **1 – Bu sprint** | Dispatcher: Lua atomik claim veya leader election + retry filtresi düzelt + concurrency token; SDK outbox finalize sırası + shutdown sırası + publisher reconnect; privilege escalation kuralı; LDAP escape; HttpWorker allow-list; SqlWorker read-only default. |
| **2 – Sonraki sprint** | Outbox/reconciliation; ortak consumer base; status transition tablosu; Options validation; prod-guard refactor; compose hardening; CI: test gating + reusable workflow + `permissions:`; 403 ve API-key/OIDC/MCP testleri. |
| **3 – Backlog** | Domain'i SDK'dan ayır, wire-DTO; teknoloji-adsız port'lar; dispatcher'ı parçala; timezone/misfire; ölü kod temizliği; doküman güncelleme; UI token storage → httpOnly cookie. |

---

Aşağıdaki bölümler her alanın tam bulgu listesini içerir.

---

# BÖLÜM A — Application & Domain


**Kapsam:** `src/Milvaion.Domain` (36 dosya, ~1.9k satır) ve `src/Milvaion.Application` (Features, Behaviours, Dtos, Utils, Interfaces – ~23k satır, ~80 handler).
**Yöntem:** Tüm handler/validator/DTO/Utils dosyaları okundu; `DateTime.Now`, `.Result`, `Task.Run`, `catch {}`, `EF.Functions`, `AsNoTracking`, `Transaction` gibi kalıplar grep ile tarandı; çapraz doğrulama için gerektiğinde `Milvaion.Infrastructure`, `Milvaion.Api` ve `Sdk` projelerine bakıldı (yalnızca doğrulama amaçlı, kapsam dışı bulgular raporlanmadı).

---

## 1. Genel Değerlendirme

**Katmanın rolü.** Domain katmanı kimlik/yetki (User, Role, Permission, ApiKey, Session), CMS (Namespace/ResourceGroup/Content/Media), UI menü/sayfa modelleri, bildirim ve tek satırlık `AppSetting` entity'lerini içerir. Zamanlayıcının asıl domain'i (`ScheduledJob`, `JobOccurrence`, `Workflow`, `WorkflowRun`, `FailedOccurrence`) ise **Domain projesinde değil, `Sdk/Milvasoft.Milvaion.Sdk/Domain` altında** yaşar; Domain projesi SDK'ya referans verir. Application katmanı MediatR tabanlı CQRS (Milvasoft `ICommandHandler/IQueryHandler` sarmalayıcıları), FluentValidation pipeline'ı, Mapster, `IMilvaionRepositoryBase<T>` soyutlaması ve Redis/RabbitMQ arayüzleri üzerinden ~80 use-case barındırır.

**Genel kalite.** Kod büyük ölçüde okunabilir ve tutarlı bir "feature folder" düzenine sahip; özellikle yeni yazılmış görünen bölümler (`GetUpcomingExecutionList`, `GetJobOccurrenceLogList/Summary`, `WorkflowStepExtensions`, `SystemResourceReader`, `UpdateWorkflow`) dikkatli tasarlanmış, sınırlandırılmış sorgular ve açıklayıcı yorumlarla yazılmış. Buna karşılık daha eski görünen bölümler (`UpdateScheduledJob`, `DeleteJobOccurrence`, `GetJobOccurenceList*`, CMS, Export) ciddi mantık hataları içeriyor. Katmanlama açısından ARCHITECTURE.md'de vaat edilen "Onion" kurallarına uyulmuyor: Domain ASP.NET Core ve EF Core'a, Application ise EF Core, Npgsql'e özgü `ILike`, `IConfiguration`, `HttpContext` ve teknoloji-adlı (`IRedis*`, `IRabbitMQPublisher`) arayüzlere doğrudan bağımlı. Birim testi yalnızca `CronProjector` için `InternalsVisibleTo` ile öngörülmüş.

**Özet sayılar:** 3 Critical, 9 High, 22 Medium, 20+ Low bulgu.

---

## 2. Bulgular

### 2.1 CRITICAL

#### C1. `UpdateScheduledJob` – `JobVersions` snapshot'ı kendi geçmişini içeriyor → jsonb boyutu her güncellemede ikiye katlanıyor
- **Dosya:** `src/Milvaion.Application/Features/ScheduledJobs/UpdateScheduledJob/UpdateScheduledJobCommandHandler.cs:95`
- **Kanıt:**
  ```csharp
  existingJob.JobVersions.Add(JsonSerializer.Serialize(existingJob));
  setPropertyBuilder.SetPropertyValue(sj => sj.JobVersions, existingJob.JobVersions);
  ```
  `ScheduledJob.JobVersions` (`Sdk/.../Domain/ScheduledJob.cs:146`) üzerinde `[JsonIgnore]` yok. `Serialize(existingJob)` çağrısı, `JobVersions` listesini (yani önceki tüm snapshot'ları) da serileştirir. n. snapshot, 1..n-1 snapshot'larının tamamını içerir → boyut geometrik büyür (30 güncellemeden sonra 2^30 × taban boyut). `AutoDisableSettings`, `JobData` ve boş navigation'lar da her seferinde kopyalanır.
- **Etki:** Cron/JobData birkaç kez düzenlenen her iş için `ScheduledJobs` satırı MB/GB seviyesine çıkar; `ScheduledJobDetailDto.Projection` (`JobVersions = r.JobVersions`) ve `GetAllAsync` ile tam entity çeken Workflow handler'ları bu veriyi her istekte okur.
- **Öneri:** Snapshot için ayrı bir hafif DTO (DisplayName, Cron, JobData, Version, ...) serileştirin ya da `JobVersions`/`Occurrences`/`FailedOccurrences` alanlarını `[JsonIgnore]` ile işaretleyin. Mevcut kayıtları temizleyen bir migration düşünün.

#### C2. `DeleteJobOccurrence` – geçmiş occurrence silmek, ilgili **işi zamanlayıcıdan (Redis ZSET) çıkarıyor**
- **Dosya:** `src/Milvaion.Application/Features/ScheduledJobs/DeleteJobOccurrence/DeleteJobOccurrenceCommandHandler.cs:36-37`
- **Kanıt:**
  ```csharp
  var jobIds = canBeDeletedOccurrences.Select(j => j.JobId).ToList();
  await _redisSchedulerService.RemoveFromScheduledSetBulkAsync(jobIds, cancellationToken);
  ```
  ZSET'in üyeleri `JobId`'dir. Tamamlanmış bir occurrence'ı silmek, aktif ve tekrarlayan ana işi ZSET'ten kaldırır. `JobDispatcherService` yalnızca **startup recovery**'de (`JobDispatcherService.cs:1104`) ZSET'i DB'den yeniden doldurur; dolayısıyla iş, API yeniden başlatılana kadar bir daha çalışmaz. Kullanıcı arayüzünde "eski çalıştırmaları temizle" gibi masum bir işlem, sessizce üretim işlerini durdurur.
- **Öneri:** Bu satırı tamamen kaldırın. Occurrence silmenin zamanlama ile ilgisi yoktur.

#### C3. `ExportService` yetki kontrolü hiç geçemez + statik izin listesi her çağrıda büyüyor
- **Dosya:** `src/Milvaion.Application/Utils/Extensions/HttpExtensions.cs:88-103`, `src/Milvaion.Application/Dtos/ExportDtos/ExportRequest.cs:47`
- **Kanıt:**
  ```csharp
  var currentUserPermissonExists = httpContext.Items.TryGetValue(GlobalConstant.CurrentUserPermissionsKey, out var contextItem);
  if (!currentUserPermissonExists) httpContext.Response.ThrowWithForbidden();
  ...
  permissions.Add(PermissionCatalog.App.SuperAdmin);
  ```
  (a) `GlobalConstant.CurrentUserPermissionsKey` ("CU-Permissions") repo genelinde **hiçbir yerde `HttpContext.Items`'a yazılmıyor** (yalnızca burada okunuyor). Sonuç: `DynamicExportToExcelAsync` her zaman 403 döner; export özelliği ölü. (b) `permissions` parametresi `ExportRequest._exportTypeQueryPairs` içindeki **statik `List<string>`**'tir; `permissions.Add(SuperAdmin)` her çağrıda bu statik listeye ekleme yapar → süreç ömrü boyunca sızıntı ve (a düzeltilirse) yetki listesi kirlenir. (c) `GetRequiredPermissions()` `IsValid()`'den önce çağrıldığı için geçersiz `ExportType` `KeyNotFoundException` fırlatır.
- **Öneri:** İzinleri HttpContext.Items yerine `ClaimsPrincipal`/token claim'lerinden okuyun; `permissions` listesini kopyalayarak (`[.. permissions, SuperAdmin]`) kullanın; `IsValid()`'i başa alın.

### 2.2 HIGH

#### H1. `UpdateScheduledJob` – bulunmayan iş için `NullReferenceException`
- **Dosya:** `UpdateScheduledJobCommandHandler.cs:37-38`
- ```csharp
  if (existingJob == null)
      return Response<Guid>.Error(existingJob.Id, MessageKey.JobNotFound);
  ```
  Null kontrolünün içinde `existingJob.Id` okunuyor → her "not found" durumu 500 olur. `Error(default, ...)` olmalı.

#### H2. `GetJobOccurenceList` ve `GetJobOccurenceListCursor` – `SearchTerm` filtresi kuruluyor ama **hiç uygulanmıyor**
- **Dosyalar:** `GetJobOccurenceListQueryHandler.cs:49-59`, `GetJobOccurenceListCursorQueryHandler.cs:49-59`
- `predicate` (`c => c.Id == guid` / `EF.Functions.ILike(c.JobName, ...)`) oluşturuluyor fakat `context.Set<JobOccurrence>().ToListResponseAsync(request, ...)` çağrısına `Where(predicate)` eklenmiyor. Arama kutusu işlevsiz; üstelik `manualCount` Redis'ten alınmaya devam ettiği için sayfa sayısı da yanlış. İki handler birebir kopya (`GetValueWithCorrectType` dahil).
- **Öneri:** `context.Set<JobOccurrence>().Where(predicate ?? (_ => true))`; ortak kodu tek bir yardımcıya taşıyın.

#### H3. `GetSystemConfiguration` – Redis bağlantı dizesi API yanıtında sızdırılıyor
- **Dosya:** `Features/Configuration/GetSystemConfiguration/GetSystemConfigurationQueryHandler.cs:50`
- ```csharp
  Redis = new RedisConfigDto { ConnectionString = _milvaionConfig.Redis.ConnectionString, ... }
  ```
  Redis connection string'i tipik olarak `password=...` içerir. Endpoint `SystemAdministration.List` iznine açık (`AdminController.cs:66`). Sadece host/port veya maskelenmiş değer döndürülmeli.

#### H4. `UpdateContent` – yeni `Media` kayıtları `ContentId`/`Value` olmadan ekleniyor
- **Dosya:** `Features/ContentManagement/Contents/UpdateContent/UpdateContentCommandHandler.cs:31-35`
- ```csharp
  await _mediaRepository.ExecuteDeleteAsync(rl => rl.ContentId == request.Id, ...);
  var newMedias = request.Medias.Value?.Adapt<List<Media>>();
  if (newMedias?.Count > 0) await _mediaRepository.BulkAddAsync(newMedias, null, cancellationToken);
  ```
  `UpsertMediaDto` (`MediaAsBase64`, `Media` (byte[]), `Type`) → `Media` entity (`Value`, `Type`, `ContentId`) Mapster ile eşlenince `Value` null ve `ContentId = 0` kalır → FK ihlali (eski medialar zaten silinmiş olduğundan veri kaybı) ya da boş medya. `CreateContent`'teki gibi elle `new Media { Value = m.Media, Type = m.Type, ContentId = request.Id }` kurulmalı.

#### H5. `DeleteMetricReport` – geçersiz downcast → `InvalidCastException`
- **Dosya:** `Features/MetricReports/DeleteMetricReport/DeleteMetricReportCommandHandler.cs:21`
- ```csharp
  return (Response<Guid>)Response.Error("Metric report not found");
  ```
  `Response.Error` bir `Response` örneği üretir; `Response<Guid>`'e cast edilemez. "Not found" senaryosu 500 döner. `Response<Guid>.Error(default, "...")` kullanılmalı.

#### H6. Rol / API anahtarı yönetiminde yetki yükseltme (privilege escalation) kontrolü yok
- **Dosyalar:** `Features/Roles/CreateRole/CreateRoleCommandHandler.cs:24`, `Features/Roles/UpdateRole/UpdateRoleCommandHandler.cs:39-44`, `Features/ApiKeys/CreateApiKey/CreateApiKeyCommandHandler.cs:36`, `Features/ApiKeys/UpdateApiKey/UpdateApiKeyCommandHandler.cs:24-28`, `Features/Users/UpdateUser/UpdateUserCommandHandler.cs:72-77`
- `PermissionIdList` doğrudan ilişki tablosuna yazılıyor; `App.SuperAdmin` dahil hiçbir izin için "çağıran bu izne sahip mi?" kontrolü yok. `RoleManagement.Update` iznine sahip bir kullanıcı kendi rolüne SuperAdmin ekleyebilir; `ApiKeyManagement.Create` sahibi SuperAdmin yetkili anahtar üretebilir. İzin id'lerinin varlığı da doğrulanmıyor (FK hatası ham exception olarak döner).
- **Öneri:** Verilebilecek izinleri çağıranın kendi izin kümesiyle sınırlayın (veya SuperAdmin atamasını yalnızca SuperAdmin'e bırakın); id'leri DB'den doğrulayın.

#### H7. Kilitlenme (lockout) zamanı yerel saatle yazılıp UTC ile karşılaştırılıyor
- **Dosyalar:** `Features/Users/UpdateUser/UpdateUserCommandHandler.cs:60`, `Features/Account/Login/LoginCommandHandler.cs:85`
- ```csharp
  setPropertyBuilder.SetPropertyValue(u => u.LockoutEnd, DateTimeOffset.Now.AddMinutes(...));   // yerel offset
  if (userLocked && DateTime.UtcNow > user.LockoutEnd.Value.DateTime)                          // offset atılıyor
  ```
  `DateTimeOffset.DateTime` offset bilgisini atar; sunucu UTC+3 ise kilit 3 saat uzar (ya da UTC-x'te erken açılır). `DateTimeOffset.UtcNow` ve `.UtcDateTime` kullanılmalı. Ayrıca `PrepareLockoutResponse` (`:105-110`) `Days` bileşenini yok sayar.

#### H8. `UpdateScheduledJob` – `JobType` yalnızca Redis cache'e yazılıyor, DB'ye yazılmıyor
- **Dosya:** `UpdateScheduledJobCommandHandler.cs:116-117`, `UpdateScheduledJobCommand.cs` (`UpdateProperty<string> JobType`)
- Entity'de `JobType` diye bir property yok (`JobNameInWorker`). `GetUpdatablePropertiesBuilder(request)` isim eşlemesiyle çalıştığı için bu alan DB'ye hiç yansımaz; buna karşılık `cacheUpdates["JobType"]` Redis hash'ine yazılır (`RedisSchedulerService.cs:430/810` hash alanı gerçekten "JobType"). 24 saatlik TTL dolunca/startup recovery'de DB'deki eski değer geri gelir → dispatch edilen job adı sessizce değişir. Aynı desen `UpdateInternalNotificationCommand` (`RelatedEntity` vs entity `RelatedEntityType`, `UpdateProperty<object> Data` vs `string`) için de geçerli. (Güven: yüksek; Milvasoft builder'ın bilinmeyen alanı sessizce yoksaydığı varsayımıyla.)

#### H9. `UpdateScheduledJob` – pasif işi yeniden aktif etmek, geçmişte kalmış `ExecuteAt` ile ZSET'e ekliyor → anında tetiklenme
- **Dosya:** `UpdateScheduledJobCommandHandler.cs:150-162`
- ```csharp
  var job = await _scheduledjobRepository.GetByIdAsync(request.Id, ...);
  await _redisSchedulerService.AddToScheduledSetAsync(job.Id, job.ExecuteAt, cancellationToken);
  ```
  Dispatcher'ın kendi yorumu (`JobDispatcherService.cs:1085-1090`) `ExecuteAt` sütununun "son create/update'te dondurulduğunu ve çalışmış her tekrarlayan iş için geçmişte olduğunu" söylüyor; recovery bu yüzden sütunu kullanmıyor. Buradaki kod ise kullanıyor: cron değişmeden `IsActive=true` yapılan bir iş anında (misfire gibi) çalışır. Cron varsa `GetNextOccurrence(UtcNow)` hesaplanmalı.

### 2.3 MEDIUM

#### M1. `UpdateScheduledJob` – yeniden aktifleştirmede auto-disable sayaçları sıfırlanmıyor
- `UpdateScheduledJobCommandHandler.cs:69-78`: `IsActive` değişince `AutoDisableSettings` yeni bir nesneyle (sayaçsız) yeniden kuruluyor ama bu nesne **yalnızca `request.AutoDisableSettings.IsUpdated` bloğunda** builder'a yazılıyor. Sadece `IsActive` gönderilirse reset kaybolur; `ConsecutiveFailureCount` eşikte kaldığı için iş ilk hatada tekrar kapanır.

#### M2. `UpdateScheduledJob` – `FixJobData` sonucu atılıyor; SignalR event verisi ölü kod
- `:56` `ScheduledJob.FixJobData(request.JobData.Value);` dönüş değeri kullanılmıyor. `:168-180` `updateData` sözlüğü hazırlanıp `_eventPublisher` hiç çağrılmıyor ("Publish SignalR event" yorumu yanıltıcı).

#### M3. `CreateScheduledJob` – cron'un gelecekte hiç tetiklemesi yoksa NRE; DB-Redis tutarsızlığı
- `CreateScheduledJobCommandHandler.cs:37` `GetNextOccurrence(...)!.Value` – Create validator'ı yalnızca `Parse` yapıyor (Update validator'ı `nextRun.HasValue` kontrol ediyor, tutarsız). Ayrıca DB insert (`:88`) sonrası Redis ekleme (`:92`) başarısız olursa iş DB'de "aktif" ama hiç zamanlanmamış kalır; startup recovery'ye kadar `NotScheduled` görünür. Transaction yok; en azından Redis hatasında telafi/uyarı gerekir.

#### M4. `CancelJobOccurrence` – workflow adımı occurrence'ları için iptal sinyali yanlış anahtarla gidiyor
- `CancelJobOccurrenceCommandHandler.cs:47` `PublishCancellationAsync(occurrence.CorrelationId, ...)`; worker ise AMQP `CorrelationId = occurrenceId` ile takip ediyor (`RabbitMQPublisher.cs:79`, `CancellationListenerService.cs:47`). Dispatcher'da `Id == CorrelationId` olduğu için sorun görünmez, ama `TriggerWorkflow` adım occurrence'larına **run'ın** CorrelationId'sini verir (`TriggerWorkflowCommandHandler.cs:48`) → bu endpoint'ten iptal edilen workflow adımı worker'da durmaz. `CancelWorkflow` (`:77`) doğru olarak `occ.Id` gönderiyor; iki handler tutarsız.

#### M5. `CancelJobOccurrence` – Redis "running" işareti worker durmadan kaldırılıyor, race var
- `:84` `MarkJobAsCompletedAsync(occurrence.JobId)` iptal isteği yayınlanır yayınlanmaz çağrılıyor; `Skip` politikalı iş henüz gerçekten durmadan yeni bir occurrence dispatch edilebilir. Ayrıca occurrence `UpdateAsync` ile tüm alanlar üzerine yazıldığı için worker'dan eşzamanlı gelen "Completed" durumu kaybolabilir (optimistic concurrency yok). Update + log insert `[Transaction]` içinde değil.

#### M6. Fire-and-forget istatistik güncellemeleri
- `CancelJobOccurrenceCommandHandler.cs:87`, `TriggerScheduledJobCommandHandler.cs:108`: `_ = Task.Run(async () => { try {...} catch { } })`. Hata sessizce yutuluyor (log yok); Trigger'da RabbitMQ publish başarısız olup occurrence `Failed` yapıldığında `Queued` sayacı geri alınmıyor → dashboard sayaçları kayar. Redis servisleri singleton olduğu için dispose sorunu yok, fakat yakalanan `cancellationToken` istek token'ı.

#### M7. Oturum yönetimi: admin şifre değiştirme / kilitleme aktif oturumları sonlandırmıyor; refresh kilitli kullanıcıya token veriyor
- `UpdateUserCommandHandler.cs:54-66` (NewPassword) ve `:46-52` (Lockout) oturum silmiyor; yalnızca rol değişiminde (`:92`) siliniyor. `RefreshLoginCommandHandler.cs:24-40` `LockoutEnd`/`IsDeleted`/`Provider` kontrolü yapmadan yeni token üretir → kilitlenen kullanıcı refresh ile süresiz devam eder.

#### M8. Root kullanıcı için brute-force koruması kapalı
- `LoginCommandHandler.cs:147` `if (user.UserName != GlobalConstant.RootUsername) UpdateAsync(...)` – en yetkili hesap için `AccessFailedCount` hiç kalıcı olmaz; kilitlenmez. Bilinçli bir "root'u kilitleme" tercihi ise en azından rate-limit/uyarı olmalı.

#### M9. Access/refresh token'lar düz metin olarak saklanıyor ve **history tablosunda süresiz tutuluyor**
- `Domain/UserSession.cs:25-30`, `Domain/UserSessionHistory.cs:22-27,64-65`; `ChangePassword`, `Logout`, `UpdateUser` her oturumu `UserSessionHistory`'ye kopyalıyor. DB sızıntısında geçerli refresh token'lar ele geçer. Hash saklayın ya da history'ye token yazmayın.

#### M10. `X-Real-IP` başlığına koşulsuz güven
- `HttpExtensions.cs:110-127`: İstemci `X-Real-IP` gönderirse oturum IP'si ve loglar sahte olur. Yalnızca güvenilir proxy'den gelen isteklerde (ForwardedHeaders middleware ile) kullanılmalı.

#### M11. `UpdateWorkflow` – kaldırılan adımların **tüm geçmiş run'lardaki** occurrence'ları siliniyor
- `UpdateWorkflowCommandHandler.cs:244` `ExecuteDeleteAsync(o => removedStepIds.Contains(o.WorkflowStepId.Value))` – Definition versiyonlanmasına rağmen çalıştırma geçmişi (loglarıyla birlikte cascade) yok ediliyor; `GetWorkflowRunAnalysis` eski run'larda adımları "not reached" gösterir. Ayrıca `:247` edge değişikliği yalnızca **sayı** ile tespit ediliyor (aynı sayıda farklı edge → versiyon artmıyor); `IsActive` değişimi ise gereksiz yere versiyon artırıyor (`:65-69`).

#### M12. `CreateWorkflow` / `UpdateWorkflow` – duplicate `TempId` sessizce adımları birleştiriyor
- `CreateWorkflowCommandHandler.cs:48`, `UpdateWorkflowCommandHandler.cs:141`: `tempIdToRealId[tempId] = ...` aynı TempId'de son yazan kazanır; iki adım aynı Guid'i alır, `ValidateDAG` de `TryAdd` ile tek düğüm sayar. Validator'da uniqueness kuralı yok. `CreateWorkflowCommandHandler.cs:78`'deki `?? Guid.CreateVersion7()` fallback'i, `:48`'dekinden farklı bir Guid ürettiği için (TempId null olsaydı) `KeyNotFoundException` verirdi; validator sayesinde ölü ama yanıltıcı.

#### M13. `TriggerWorkflow` – `step.JobId!.Value` NRE riski; occurrence'lar `JobName/JobDisplayName` olmadan yaratılıyor
- `TriggerWorkflowCommandHandler.cs:62`. Create/Update `JobId == Guid.Empty` ise `JobId = null` yazar (`CreateWorkflowCommandHandler.cs:63`); `Task` tipinde JobId'siz bir adım tetiklemede çöker.

#### M14. Job listelerinde Redis sayaçlarıyla toplam kayıt sayısı, filtrelerle uyumsuz
- `GetJobOccurenceListQueryHandler.cs:33-48`: Yalnızca `JobId` filtresi hariç tutuluyor; `WorkerId`, tarih aralığı, `Status`+`SearchTerm` kombinasyonlarında `TotalDataCount` Redis'ten gelen global sayı oluyor → sayfalama yanlış. `(int)long` cast'leri de taşma riski.

#### M15. `GetScheduledJobDetailDto` – occurrence tablosu üzerinde üç korelasyonlu alt sorgu + şişmiş `JobVersions`
- `ScheduledJobDetailDto.cs:153-155` `r.Occurrences.Average/Count/Count(...)`; milyonlarca occurrence'ı olan iş için detay ekranı ağır. `JobVersions = r.JobVersions` C1 ile birleşince tehlikeli. İstatistikleri Redis'ten (zaten var) alın, versiyonları ayrı endpoint'e taşıyın.

#### M16. `JobOccurrenceDetailDto.Projection` – `Logs = r.Logs` sınırsız log yüklüyor ve entity'yi DTO'da sızdırıyor
- `JobOccenceDetailDto.cs:123`. Ayrı, sayfalı `GetJobOccurrenceLogList` zaten mevcut; detayda ilk N log veya hiç log döndürülmeli.

#### M17. Workflow handler'ları `ScheduledJob` entity'sini projeksiyonsuz çekiyor
- `GetWorkflowDetailQueryHandler.cs:29`, `GetWorkflowRunDetailQueryHandler.cs:49`, `GetWorkflowRunAnalysisQueryHandler.cs:36`, `CreateWorkflowCommandHandler.cs:20`, `UpdateWorkflowCommandHandler.cs:113` – sadece `DisplayName/WorkerId/JobNameInWorker` için `JobData`, `JobVersions` (C1), `AutoDisableSettings` jsonb'leri okunuyor. `Select`/projection kullanın.

#### M18. `GetLatestMetricReport` / `DeleteOldMetricReports` – tüm tabloyu belleğe alıyor
- `GetLatestMetricReportQueryHandler.cs:15-24`: tipe ait **tüm** raporlar (payload dahil) çekilip bellekte sıralanıyor; `OrderByDescending(...).FirstOrDefault()` DB'de yapılmalı. `DeleteOldMetricReportsCommandHandler.cs:11-15`: silinecek satırlar tam entity olarak yükleniyor; `ExecuteDeleteAsync` yeterli.

#### M19. CMS denormalize alanlar güncellenmiyor
- `UpdateNamespaceCommandHandler.cs` / `UpdateResourceGroupCommandHandler.cs`: `Name` değişince `Slug` yeniden hesaplanmıyor; `Content.NamespaceSlug/ResourceGroupSlug/KeyAlias` kopyaları asla senkronlanmıyor → `GetContent` (slug ile arar) eski veriyle çalışır. `CreateBulkContentCommandHandler.cs:38` `request.Adapt<Content>()` (komutun kendisi) adaptlanıyor, `requestContent` değil; geçersiz `ResourceGroupId`'ler sessizce atlanıyor (`:23-24`); `BulkAddAsync` ile `Medias` graph'ının yazılıp yazılmadığı belirsiz.

#### M20. Validator'larda yeniden kullanılan `DateTime.UtcNow` yapıcı zamanında değerlendiriliyor
- `CreateScheduledJobCommandValidator.cs:58` `.LessThan(DateTime.UtcNow.AddYears(1))`, `CreateApiKeyCommandValidator.cs` `.GreaterThan(DateTime.UtcNow)`. Validator scoped kaydedildiği sürece zararsız, ama `Must(d => ...)` ile per-validation yazılmalı (bugün kaydın lifetime'ına gizli bağımlılık var).

#### M21. `ValidationBehaviorForResponse` – `CancellationToken` iletilmiyor, `IResponseInterceptionOptions` null olabilir
- `ValidationBehaviour.cs:37` `validator.ValidateAsync(context)`; `:85-91` `GetService<IResponseInterceptionOptions>()` sonucu null-check'siz kullanılıyor.

#### M22. Domain katmanı ASP.NET Core / DI / EF Core'a bağımlı
- `Domain/User.cs:1-3,111-116` `GetCurrentUser(IServiceProvider)` → `IHttpContextAccessor`; `[Column(TypeName="jsonb")]` (Npgsql'e özgü) ve `Microsoft.EntityFrameworkCore.Index` attribute'ları; `Domain.csproj` `Microsoft.AspNetCore.DataProtection`, `Milvasoft.DataAccess.EfCore.Bulk` referansları. ARCHITECTURE.md "Domain has no dependencies on other layers" diyor. `GetCurrentUser` bir `ICurrentUserProvider` soyutlamasına taşınmalı.

### 2.4 LOW

- **L1.** `Dtos/AccountDtos/InternalNotifications/**` – `Features/Account/InternalNotifications/**` ile **birebir kopya** 9 dosya (yalnızca namespace farklı). MediatR her iki komut tipini de kaydeder; ölü kod. Ayrıca `Features/ScheduledJobs/GetJobOccurenceListWithCursor/`, `Dtos/YargitayDtos/` boş klasörler ("Yargıtay" başka bir Milvasoft projesinden kalma), `Milvaion - Backup.Application.csproj` aynı klasörde ikinci bir csproj (eski Milvasoft 8.x sürümleriyle; `dotnet build` klasörde çalıştırılırsa "multiple project files" hatası). `src/Milvaion.Api.SqlServer` boş klasör; kod (`EF.Functions.ILike`, `jsonb`) yalnızca PostgreSQL'i destekliyor.
- **L2.** `AccountNotificationDto.cs:1,30`, `InternalNotificationDetailDto.cs:1,31`, `InternalNotificationListDto.cs:1,30` – `using Microsoft.EntityFrameworkCore.Metadata.Internal;` yalnızca `nameof(EntityType)` derlensin diye eklenmiş; `LinkedWith` DTO'da olmayan "EntityType" alanına bağlanıyor (doğrusu `nameof(Type)`), `TypeDescription` formatlama muhtemelen çalışmıyor.
- **L3.** Yanlış aktivite etiketleri: `DeleteWorkerCommandHandler.cs:13` `UserActivity.DeleteScheduledJob`, `UpdateLanguageCommandHandler.cs:18` `UserActivity.UpdateRole` (enum'da `UpdateLanguages` var).
- **L4.** `GetFailedOccurrenceListQueryHandler.cs:23-24` `searchTerm = $"%{...}%"` sonra `IsNullOrWhiteSpace(searchTerm)` → hiçbir zaman boş değil; arama yokken de üç `ILIKE '%%'` çalışıyor.
- **L5.** `TriggerScheduledJobCommandHandler.cs:83` `request.Reason ?? "Manual execution by " + name ?? "Anonymous"` – öncelik nedeniyle `"Anonymous"` hiç kullanılmaz. `UpdateFailedOccurrenceCommandHandler.cs:35` "Anonymus" yazım hatası. `DeleteJobOccurrenceCommandValidator.cs:16` `MessageKey.OccurrenceId` localizer'dan geçirilmeden ham anahtar olarak mesaja giriyor.
- **L6.** `CreateScheduledJobCommandValidator.cs:29-32` ve `:47-50` aynı cron kuralı iki kez; `NotBeMoreFrequentThan1Minute` ölü. `UpdateScheduledJobCommandValidator.BeInFuture` ölü. `IsValidForExternalUpdate` `JobData.IsUpdated`'ı iki kez kontrol ediyor.
- **L7.** `CancelJobOccurrenceCommandHandler.cs:69` `(int)(...).TotalMilliseconds` – `DurationMs` `long?` iken int'e cast (24 günden uzun sürede taşar).
- **L8.** `LoginCommandValidator` `UserName` için kural içermiyor; `CreateUserCommandValidator.cs:22` `MaximumLength(70)` entity `MaxLength(100)` ile tutarsız ve Surname mesajı `GlobalName` anahtarını kullanıyor; `Email` doğrulanmıyor (extension mevcut). `AccountDetail` UserNotFound/Unauthorized ayrımı kullanıcı numaralandırmasına (enumeration) izin veriyor; `LockWarning` mesajı hesap varlığını doğruluyor.
- **L9.** `ILike` desenlerinde kullanıcı girdisindeki `%`/`_` kaçışlanmıyor (`GetScheduledJobList`, log sorguları, `GetUpcomingExecutionList`) – wildcard enjeksiyonu (DoS riski düşük).
- **L10.** `UserActivityLogInterceptor.cs:15` scoped repository yapıcıda `GetService` ile alınıyor; `:52` tüm exception'lar loglanmadan yutuluyor; `AddAsync(entity)` token'sız.
- **L11.** `Domain/ActivityLog.cs:33` `[MaxLength(255)]` bir `DateTimeOffset` alanında anlamsız. `TableNames` içinde `MenuItemTranslations/MenuGroupTranslations/PageActionTranslations/ApiLogs/MethodLogs` gibi entity'si olmayan tablolar. `Domain/InternalNotification.cs` yorumu "60/30 günde silinir" diyor; bu kural Application'da yok (Infrastructure'da olabilir).
- **L12.** `ApiKeyOptions.cs:32` `Encoding.ASCII.GetBytes(Secret)` – ASCII dışı karakter içeren gizli anahtar '?' ile bozulur, entropi düşer; `UTF8` kullanın.
- **L13.** `GetWorkerListQueryHandler.cs:18-22` `new ListResponse<WorkerDto>{...}` – `IsSuccess/StatusCode` elle set edilmiyor; Milvasoft `Response` varsayılanı false ise liste "başarısız" döner (güven: düşük, kütüphane kaynağı yok). `ListResponse<T>.Success(...)` kullanın.
- **L14.** `GetDashboardQueryHandler.cs:22-28` Redis `long` değerleri `(int)` cast; `GetTagListQueryHandler` tag'leri trim etmiyor.
- **L15.** `HttpExtensions.GetCurrentUserClaims` her çağrıda JWT'yi yeniden doğruluyor (`IMilvaTokenManager.GetClaimsPrincipalIfValid`) – `HttpContext.User.Claims` zaten mevcut.
- **L16.** `RuleBuilderOptionsExtensions.PhoneNumber` `^\+90\d{10}$` – yalnızca Türkiye; global bir üründe validator'a sızmış lokal kural.
- **L17.** `UpdateUserCommandHandler.cs:33-42` kullanıcı yoksa `ExecuteUpdate` 0 satır etkiler ve `Success` döner; `RoleIdList` gönderilmişse `:91` `user.UserName` NRE.
- **L18.** `ScheduledJob.JobData`, `WorkflowRun.StepJobData` gibi alanlar loglarda/`JobOccurrenceLog.Data` içinde düz metin (`TriggerScheduledJobCommandHandler.cs:86 "CustomJobData"`) – iş verisi sır içeriyorsa loglara sızar.

---

## 3. Dokümantasyon ile Çelişkiler (ARCHITECTURE.md)

| Doküman iddiası | Koddaki durum |
|---|---|
| "Domain has no dependencies on other layers" (satır 42) | `Domain.csproj` → `Milvasoft.Milvaion.Sdk`, `Microsoft.AspNetCore.DataProtection`, `Milvasoft.DataAccess.EfCore.Bulk`; `User.cs` `IHttpContextAccessor` kullanıyor. |
| "Application depends only on Domain" (satır 43) | 14 dosyada `using Microsoft.EntityFrameworkCore`; `EF.Functions.ILike` (Npgsql), `IMilvaBulkDbContextBase`, `IConfiguration`, `HttpContext`, `Milvasoft.Caching.Redis`, `Milvasoft.Interception.Ef` paketleri. |
| Domain bileşenleri: `ScheduledJob`, `JobOccurrence`, `FailedOccurrence`, `Worker`, `MilvaionUser` (satır 60-70) | Bunlar `Sdk/Milvasoft.Milvaion.Sdk/Domain` altında; `Worker` entity'si yok (Redis `CachedWorker`); kullanıcı entity'si `User`. |
| "Background Services" `Milvaion.Api` altında (satır 122) | `Milvaion.Infrastructure/BackgroundServices`. |
| "PostgreSQL / SQL Server" (görev tanımı) | Yalnızca PostgreSQL (`UseNpgsql`, `jsonb`, `ILike`); `Milvaion.Api.SqlServer` boş klasör. |
| CQRS örneği `record CreateJobCommand(CreateJobDto Dto) : IRequest<Response<JobDto>>` | Gerçek kalıp Milvasoft `ICommand<T>/ICommandHandler<T>` + düz property'li record'lar. |

---

## 4. Mimari İyileştirme Önerileri

1. **Katman sınırlarını gerçekten çizin.** Application'daki `IRedis*`/`IRabbitMQPublisher` arayüzlerini teknoloji-bağımsız isimlere (`IJobScheduleStore`, `IJobQueue`, `IWorkerRegistry`, `IExecutionStats`) taşıyın; `IMilvaionDbContextAccessor`/`IMilvaBulkDbContextBase` ve `EF.Functions` kullanımlarını repository/specification arkasına alın (arama için `ISearchable` benzeri bir soyutlama). Domain'den `IServiceProvider`/`IHttpContextAccessor`'ı çıkarın (`ICurrentUser`).
2. **Zamanlayıcı domain'ini tek yerde toplayın.** `ScheduledJob`/`JobOccurrence`/`Workflow` SDK'da, User/Role Domain'de; "hangi entity nerede" belirsiz. SDK yalnızca worker sözleşmesini (mesajlar, `IJob`, `JobContext`) taşımalı; entity'ler Domain'e dönmeli.
3. **Zengin domain / invariantlar.** Durum geçişleri (`Queued→Running→Completed/Cancelled`), `AutoDisableSettings` sıfırlama, `JobVersions` snapshot üretimi, `KeyAlias`/slug hesaplama gibi kurallar handler'larda kopyalanıyor. Bunları entity metotlarına (`job.Reactivate(now)`, `job.Snapshot()`, `occurrence.Cancel(by, reason, now)`) taşıyın; böylece H9/M1/C1/M19 türü hatalar tek noktada çözülür.
4. **DB + Redis tutarlılığı için outbox / telafi.** Create/Update/Delete job handler'ları DB ve Redis'i ayrı ayrı güncelliyor (`[Transaction]` Redis'i kapsamaz). Ya Redis yazımını commit sonrasına (domain event + outbox) alın ya da dispatcher'ın periyodik bir reconciliation döngüsü olsun (bugün yalnızca startup'ta var).
5. **Ortak liste altyapısı.** `GetJobOccurenceList` / `...Cursor`, üç `Get*List` handler'ındaki `SearchTerm` kalıbı ve `GetValueWithCorrectType` kopyaları tek bir `SearchableListQuery` yardımcısına indirgenmeli; `SearchTerm` uygulanmaması gibi hatalar kopya sayısıyla çoğalıyor.
6. **Yetki modeli.** İzin verme işlemlerini (rol/ApiKey/kullanıcı) "delegable permissions" kuralına bağlayın; JWT claim'lerine gömülü izinler rol güncellemesinde geçersiz kılınmıyor (Login `ClaimTypes.Role` + `IUIService`) – ya kısa ömürlü token + refresh'te yeniden hesaplama, ya da izin kontrolünü DB/cache'ten yapın.
7. **Ölü kod ve kalıntı temizliği.** Dtos altındaki kopya handler'lar, boş klasörler, Backup csproj, ölü validator metotları, `updateData` gibi kullanılmayan bloklar, yanlış `UserActivity` etiketleri. Bir analyzer kuralı (IDE0051/IDE0060) ve `TreatWarningsAsErrors` bu birikimi engeller.
8. **Test kapsamı.** Yalnızca `CronProjector` test edilebilir görünüyor. En azından `UpdateScheduledJob`, `DeleteJobOccurrence`, `Login/Lockout`, `WorkflowStepExtensions`, `ValidationBehavior` için in-memory repository ile handler testleri; DTO projection'ları için EF Core "can translate" testleri (örn. `ScheduledJobDetailDto`, `GetGroupedContentList`) eklenmeli.
9. **Gözlemlenebilirlik.** Fire-and-forget bloklarda `catch {}` yerine `IMilvaLogger` ile logla; `UserActivityLogInterceptor` da aynı şekilde.

---

# BÖLÜM B — Infrastructure


Kapsam: `BackgroundServices/*`, `Scheduling/*`, `Persistence/*`, `Services/*` (Redis, RabbitMQ, Alerting, Monitoring, Settings, Identity/LDAP, Admin, Developer, vb.), `Workflows/ConditionExpression`, `InternalJobs`, `HealthChecks`, `Telemetry`, `InfraServiceCollectionExtensions` (~17.3k satır) ve `Milvaion.Application/Interfaces` altındaki Redis/RabbitMQ sözleşmeleri. Doğrulama için `docs/portaldocs/08-reliability.md`, `09-scaling.md`, `docs/githubdocs/ARCHITECTURE.md`, SDK entity'leri (`JobOccurrence`, `ScheduledJob`) ve `Milvaion.Api/AppStartup/Program.cs` + `Migrations/MigrationHostedService.cs` de okundu.

Bütün dosyalar okunmuştur; aksi belirtilmedikçe bulgular kaynak kod üzerinde doğrulanmıştır. Emin olunmayan noktalar "(güven: orta/düşük)" ile işaretlenmiştir.

---

## 1. Katmana Genel Bakış ve Kalite Değerlendirmesi

**Mimari.** Katman, "Redis ZSET = canlı zamanlama, PostgreSQL = kayıt defteri, RabbitMQ = taşıma" modeline oturuyor. `JobDispatcherService` ZSET'ten vadesi gelen job'ları çekip per-job Redis lock ile `JobOccurrence` yaratıyor ve topic exchange'e publish ediyor; `StatusTrackerService` worker status mesajlarını batch'leyip DB'ye yazıyor; `WorkflowEngineService` DAG'ı polling ile ilerletiyor; `ZombieOccurrenceDetectorService`/`JobMisfireMonitorService` sağlık kontrolleri; `WorkerAutoDiscoveryService` Redis-tabanlı worker registry'yi besliyor. Redis erişimi tek bir `RedisCircuitBreaker` ile sarılmış, RabbitMQ tek connection + per-operasyon channel modelinde.

**Güçlü yanlar.**
- Bulk lock alma/bırakma ve owner-check'li release için Lua script kullanımı doğru.
- StatusTracker'da ACK'nin DB persist sonrasına ertelenmesi (at-least-once) doğru tasarım.
- Publisher confirms + `mandatory` ile route edilemeyen mesajın yakalanması güzel bir detay.
- `StartupScheduleResolver`'ın "Redis'teki score'u ezme" kuralı ve `CompletedAt` ile one-time job'ın ikinci kez çalışmasının engellenmesi, geçmişte yaşanmış bir sorunun düzgün çözümü.
- `ConditionExpression` gerçek bir tokenizer/recursive-descent parser; `eval` benzeri bir enjeksiyon yüzeyi yok.
- EF tarafında büyük ölçüde `AsNoTracking` + projection + `EFCore.BulkExtensions` kullanılmış; raw SQL'ler sabit string, parametreli (`SqlQueryRaw(sql, sevenDaysAgo)`), enjeksiyon yok.
- Tüm background service'ler `Task.Delay(…, stoppingToken)` ile iptal edilebilir; `OperationCanceledException` ayrımı yapılıyor.

**Zayıf yanlar (özet).**
- **Dokümantasyon ile kodun uyuşmaması:** `09-scaling.md` ve `ARCHITECTURE.md` "dispatcher leader election / `locks:dispatcher`" anlatıyor; kodda böyle bir kilit **yok**. Çoklu instance'ta hem dispatcher hem workflow engine hem zombie detector bağımsız çalışıyor.
- **Optimistic concurrency yok:** `JobOccurrence` üzerinde hiçbir concurrency token yok (`RowVersion|xmin` araması sıfır sonuç), ama üç servis "RowVersion ile BulkUpdate" yorumlarıyla `DbUpdateConcurrencyException` yakalıyor — bu handler'lar ölü kod, lost update gerçek.
- Dispatch-retry mekanizması bir status filtre hatası yüzünden hiç çalışmıyor.
- RabbitMQ consumer'larının kanal ölümünden toparlanma stratejisi yok; bazı servislerde ise kanal ölümü `throw` ile host'u kapatabiliyor.
- Circuit breaker'ın "sessiz fallback" tasarımı, publish sonrası Redis yazma hatalarını yutarak duplicate dispatch'e yol açabiliyor.
- Çok sayıda `_ = Task.Run(...)` fire-and-forget; bazı loop'lar (LogCollector batch task) gözlemlenmeyen task olarak sessizce ölebilir.
- Servis-lokator (`IServiceProvider` enjeksiyonu) ve iki ayrı Redis multiplexer'ı gibi yapısal tutarsızlıklar.

Genel not: Kod okunabilir, yorumlu ve niyeti belli; ancak **dağıtık doğruluk** garantileri büyük ölçüde tek instance varsayımına dayanıyor ve bu varsayım dokümanlarda tersine anlatılıyor.

---

## 2. Bulgular

Şiddet ölçeği: **Critical** = veri kaybı / duplicate execution / güvenlik ihlali; **High** = önemli işlevsel bozukluk veya kolay tetiklenen kesinti; **Medium** = kenar durum, ops sorunu, drift; **Low** = kalite/temizlik.

### 2.1 CRITICAL

#### C1. Dispatcher'da leader election yok; çoklu instance'ta aynı job iki kez dispatch edilebilir
- **Dosya:** `BackgroundServices/JobDispatcherService.cs:206-281` (`DispatchDueJobsAsync`), `:887-934` (`DispatchJobToRabbitMQAsync`), `Services/Redis/RedisSchedulerService.cs:154-185` (`GetDueJobsAsync`), `Services/Redis/RedisLockService.cs:197-241`.
- **Alıntı:**
  ```csharp
  // 1. Get due jobs from Redis ZSET  (ZRANGEBYSCORE – non-destructive)
  var dueJobIds = await GetDueJobIdsFromRedisAsync(cancellationToken);
  ...
  // 5. Acquire locks and create occurrences
  var lockResults = await _redisLock.TryAcquireLocksBulkAsync(jobIdsToLock, Environment.MachineName, ...);
  ...
  await HandleRecurringJobAsync(job, cancellationToken);   // ZADD next score
  finally { await _redisLock.ReleaseLockAsync(job.Id, Environment.MachineName, ...); }
  ```
- **Neden:** `GetDueJobsAsync` sadece okur (`ZRANGEBYSCORE`), claim etmez. Instance A ve B aynı anda `[X]` listesini okur. A kilidi alır, occurrence yazar, publish eder, `UpdateScheduleAsync` ile X'in score'unu ileri atar ve kilidi **hemen** bırakır (`finally`). B, kilidi A bıraktıktan sonra dener → başarılı → kilit altında ZSET score'unu **yeniden kontrol etmediği** için X için ikinci bir occurrence yaratır ve tekrar publish eder. Pencere, `GetDueJobs` ile `TryAcquireLocksBulk` arasındaki süre (worker kapasite sorguları per-worker sıralı Redis çağrıları içerir, yüzlerce ms olabilir) kadardır ve polling interval'ı 10 s olan iki instance'ta düzenli olarak çakışır. Dokümanlar (`09-scaling.md:149-157`, `ARCHITECTURE.md:341,452-470`) tek aktif dispatcher ve `locks:dispatcher` anahtarı vaat ediyor; kodda `TryAcquireNamedLockAsync` yalnızca `startup_recovery` için kullanılıyor.
- **Düzeltme:** İki seçenek: (a) Gerçek leader election: `TryAcquireNamedLockAsync("dispatcher", ttl)` + periyodik `ExtendLockAsync`; kaybeden instance dispatch döngüsünü atlar. (b) Kilitsiz, atomik claim: tek bir Lua script ile `ZRANGEBYSCORE` + her üye için `ZADD` (cron'a göre bir sonraki score) veya `ZREM` (one-time) yapıp claim edilen id'leri döndürmek; DB/RabbitMQ hatası olursa score'u geri almak. (b) tercih edilirse per-job lock'a gerek kalmaz. En azından kilit alındıktan sonra `SortedSetScoreAsync` ile score'un hâlâ `<= now` olduğu doğrulanmalı.

#### C2. Dispatch-retry mekanizması ölü: publish hatası occurrence'ı `Failed` yapıyor, retry sorgusu ise `Queued` arıyor
- **Dosya:** `JobDispatcherService.cs:894-915`, `:643-644`, `:738-741` (`_updatePropNames` `Status` içeriyor), `:72-81` (`_getRetryOccurrencesCompiled`), `:1289-1490`.
- **Alıntı:**
  ```csharp
  if (!published) { occurrence.Status = JobOccurrenceStatus.Failed; occurrence.Exception = "Failed to publish to RabbitMQ"; ... return false; }
  ...
  await jobOccurrenceRepository.BulkUpdateAsync([.. failedToDispatchOccurrences], bc => { bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _updatePropNames; /* Status dahil */ });
  ...
  EF.CompileAsyncQuery((ctx, now, maxRetries) => ctx.JobOccurrences.Where(o => o.Status == JobOccurrenceStatus.Queued && o.NextDispatchRetryAt != null && ...))
  ```
- **Neden:** `HandleFailedDispatchesAsync` `NextDispatchRetryAt` ayarlar ama `Status=Failed` olarak persist eder; `RetryFailedDispatchesAsync` yalnızca `Status==Queued` satırlarını çeker → hiçbir zaman retry olmaz. Sonuçları: (1) One-time job publish hatasında `RemoveFromScheduledSetAsync` ile ZSET'ten silinir, `CompletedAt` boş kalır → job sadece bir sonraki **restart**'ta `StartupScheduleResolver` ile geri gelir; ondan önce kayıp. (2) Cron job'da `HandleRecurringJobAsync` çağrılmadığı için score geçmişte kalır → bir sonraki poll'da yeni bir occurrence + yeni publish denemesi; her başarısız poll için `Failed` bir occurrence satırı birikir (her 10 s'de bir). (3) `Queued` sayacı azaltılır ama `Failed` sayacı artırılmaz → stats drift. Ek olarak `JobDispatcherService.cs:900-912` publish hatasında yazılan log mesajı yanlış ("Failed to acquire dispatch lock - another instance is handling this job").
- **Düzeltme:** Publish hatasında status'u `Queued` bırakıp yalnızca `NextDispatchRetryAt`/`DispatchRetryCount`/`Exception` güncellemek (ya da ayrı bir `DispatchPending` status'u); cron job için retry süresince ZSET score'unu `NextDispatchRetryAt`'e taşımak ki dispatcher paralel yeni occurrence üretmesin; one-time job için ZSET'ten silmeyip score'u retry zamanına çekmek. Log mesajını düzeltmek.

#### C3. `WorkflowEngineService` çoklu instance'ta koordinasyonsuz: duplicate step dispatch ve duplicate cron run
- **Dosya:** `BackgroundServices/WorkflowEngineService.cs:136-189` (`CheckCronWorkflowsAsync`), `:194-318` (`ProcessWorkflowRunsAsync`), `:839-937` (`DispatchStepsAsync`).
- **Alıntı:**
  ```csharp
  var activeRuns = await dbContext.WorkflowRuns.AsNoTracking().Include(r => r.StepOccurrences)...
      .Where(r => r.Status == WorkflowStatus.Pending || r.Status == WorkflowStatus.Running).ToListAsync(...);
  ...
  await dbContext.BulkUpdateAsync(dispatches.Select(d => d.Occurrence).ToList(), ...); // Status=Queued, StepStatus=Running
  var published = await _rabbitMQPublisher.PublishJobAsync(jobForDispatch, occ.Id, cancellationToken);
  ```
- **Neden:** Hiçbir Redis lock veya `UPDATE … WHERE StepStatus = Pending` tarzı koşullu atomik claim yok. İki instance aynı 5 s'lik pencerede aynı `Pending` step'i görüp ikisi de publish eder → aynı `occ.Id` ile iki mesaj, iş iki kez çalışır. `CheckCronWorkflowsAsync`'te `LastScheduledRunAt` okunup `TriggerWorkflowCommand` gönderildikten sonra güncelleniyor; iki instance aynı slotta iki run yaratır.
- **Düzeltme:** Engine'i named lock (`workflow_engine`) ile tek instance'a indirmek **veya** step claim'i DB'de atomikleştirmek: `ExecuteUpdateAsync(o => o.StepStatus == Pending && o.Id == id → Running)` dönen satır sayısı 1 ise publish; cron için `UPDATE Workflows SET LastScheduledRunAt = @now WHERE Id=@id AND LastScheduledRunAt = @seen` guard'ı.

#### C4. `JobOccurrence` üzerinde concurrency token yok → StatusTracker / ZombieDetector / WorkflowEngine / Dispatcher arasında lost update
- **Dosya:** `Sdk/.../Domain/JobOccurrence.cs` (token yok), `StatusTrackerService.cs:361-379, 410-430, 669-681`, `ZombieOccurrenceDetectorService.cs:218-292, 297-364`, `WorkflowEngineService.cs:285-286`, `JobDispatcherService.cs:1178-1211`.
- **Alıntı:**
  ```csharp
  // BulkUpdate with RowVersion concurrency check   ← yorum; entity'de RowVersion yok
  return dbContext.BulkUpdateAsync(occurrences, (bc) => { bc.PropertiesToIncludeOnUpdate = _updatePropNames; }, ...);
  ...
  catch (DbUpdateConcurrencyException concurrencyEx) { ... }   // hiç fırlatılmaz
  ```
- **Neden:** Bütün yazıcılar `AsNoTracking` read → in-memory mutate → `BulkUpdate` (koşulsuz `UPDATE … SET … WHERE Id=`) deseninde. Senaryolar: (a) İki API instance'ı `status_updates` kuyruğunu competing-consumer olarak tüketir; A `Running`'i, B `Completed`'ı alır; B önce yazar, A'nın batch'i (500 ms gecikmeli) sonra `Running` yazarsa — evet, `_finalStatuses` guard'ı DB'den okunan `previousStatus`'a bakar, A'nın okuması B'nin yazımından **önce** yapıldıysa guard geçer ve `Completed` → `Running` olur; job sonsuza kadar `Running` görünür, zombie detector 10 dk sonra `Unknown` yapar. (b) ZombieDetector `Running` occurrence'ları okur, per-occurrence Redis çağrıları yapar (saniyeler), bu arada worker `Completed` yazar; detector sonra `Unknown` + `Exception` ile ezer. (c) WorkflowEngine retry için `Status=Queued, Result=null` yazarken StatusTracker aynı satıra `Completed` yazmış olabilir. Yorumlar ve `DbUpdateConcurrencyException` catch'leri yanlış güven veriyor.
- **Düzeltme:** `JobOccurrence`'a `uint Version` (`UseXminAsConcurrencyToken`) veya `[Timestamp]` eklemek; BulkExtensions token kontrolü yapmadığından final-state geçişlerini `ExecuteUpdateAsync(... WHERE Status IN (Queued, Running))` gibi **koşullu** güncellemelere çevirmek; en azından StatusTracker'da `UPDATE … WHERE Status NOT IN (final)` guard'ı.

### 2.2 HIGH

#### H1. Startup recovery, kümedeki tüm aktif occurrence'ları "System restart" diyerek `Failed` yapıyor
- **Dosya:** `JobDispatcherService.cs:1153-1236` (`CleanupZombieOccurrencesAsync`), `:1075-1077`.
- **Alıntı:** `Where(o => zombieStatuses.Contains(o.Status) && o.CreatedAt < zombieThreshold /* now-2m */)` → `Status = Failed; Exception = "System restart detected. Job was not completed before shutdown."`
- **Neden:** Worker'lar ayrı process; bir API pod'unun rolling restart'ı, worker'larda hâlâ koşan 2 dakikadan eski **tüm** occurrence'ları (ve RabbitMQ'da bekleyip birazdan tüketilecek `Queued` olanları) `Failed` işaretler. Named lock yalnızca eşzamanlı recovery'yi engeller, semantik yanlış kalır. Worker'ın gelecek `Completed` mesajı DB'yi geri düzeltebilir (final→final geçişi engellenmiyor) ama arada UI yanlış gösterir, `Failed` sayacı bozulur, `Queued` mesaj işlenip job `Failed` görünürken çalışır. `ZombieOccurrenceDetectorService` zaten heartbeat-tabanlı ve doğru kriteri uyguluyor.
- **Düzeltme:** Startup'ta bu adımı kaldırmak (ya da yalnızca `WorkerId` boş/aktif olmayan worker'lara ait ve `LastHeartbeat` eşik dışı olanlara sınırlamak) ve işi ZombieDetector'a bırakmak.

#### H2. Emergency stop yalnızca yerel process'i durduruyor
- **Dosya:** `Services/DispatcherControlService.cs:15-73`, `Services/AdminService.cs:89-124`, `JobDispatcherService.cs:127-138`.
- **Neden:** `_enabled` bir `volatile bool`; Redis'e yazılmıyor, pub/sub yok. N instance'lı kurulumda "Emergency stop" isteğini hangi pod aldıysa yalnızca o durur; `GetSystemHealthAsync` da yalnızca yerel durumu raporlar. Kritik bir ops kontrolü için yanıltıcı.
- **Düzeltme:** Durumu Redis anahtarında tutmak (`dispatcher:paused` + sebep/kullanıcı) ve dispatcher döngüsünde okumak; `SettingsProvider`'daki pub/sub invalidation deseni buraya da uygulanabilir.

#### H3. Consumer kapasite limiti hiç uygulanmıyor: okuma ve yazma farklı Redis anahtarlarına gidiyor
- **Dosya:** `Services/Redis/RedisWorkerService.cs:526-571` (`GetConsumerCapacityAsync` → `workers:{w}:job_counts`), `:605-699` (`BatchUpdateConsumerJobCountsAsync` → `workers:{w}:instances:{i}:job_counts`), `:574-602` (Increment/Decrement – hiçbir yerden çağrılmıyor), `JobDispatcherService.cs:790-804`.
- **Neden:** StatusTracker per-instance hash'e yazar, dispatcher per-worker hash'i okur → `currentJobs` daima 0 → `MaxParallelJobs` per job-type limiti (worker metadata `JobConfigs`) hiçbir zaman devreye girmez. `IncrementConsumerJobCountAsync`/`DecrementConsumerJobCountAsync` ölü kod. Ayrıca `RemoveWorkerInstanceAsync` yorumunda "no per-instance keys anymore" deniyor ama per-instance key yazılıyor ve silinmiyor (3600 s TTL ile temizleniyor).
- **Düzeltme:** Tek bir anahtar şeması seçmek (per-worker hash + Lua ile floor'lu HINCRBY), ölü metodları kaldırmak, `GetConsumerCapacityAsync`'in toplamı instance'lar üzerinden hesaplamasını sağlamak; dispatcher'ın per-worker `IsWorkerActive` + `GetWorkerCapacity` ikili `GetWorkerAsync` çağrısını tek `GetAllWorkersAsync` ile değiştirmek.

#### H4. RabbitMQ consumer kanalları ölünce toparlanma yok; bazı servislerde kanal ölümü host'u kapatıyor
- **Dosya:** `StatusTrackerService.cs:192-267` (`ChannelShutdownAsync` yalnızca log), `LogCollectorService.cs:136-178`, `FailedOccurrenceHandler.cs:63-111` (`throw`), `WorkerAutoDiscoveryService.cs:96-109, 174-179` (`throw new InvalidOperationException("... Reconnecting...")` ama üstte retry döngüsü yok), `Extensions/RabbitMQChannelExtensions.cs:23-65`.
- **Neden:** (a) StatusTracker/LogCollector `await Task.Delay(Timeout.Infinite, stoppingToken)` ile bekler; kanal broker tarafından kapatılırsa (ör. connection recovery sonrası eski `DeliveryTag`'lerin ACK'lenmesi → `PRECONDITION_FAILED - unknown delivery tag`; batch'te 500 ms boyunca tag tutulduğu için ağ kesintisinde muhtemel) consumer sessizce ölür, retry döngüsüne hiç dönülmez; `RabbitMQHealthCheck` yalnızca connection'a bakar → "healthy". (b) `WorkerAutoDiscoveryService` ve `FailedOccurrenceHandler` istisnayı `ExecuteAsync`'ten dışarı fırlatıyor; .NET 6+ `BackgroundServiceExceptionBehavior.StopHost` varsayılanı ile **tüm API process'i** durur (yapılandırmada `Ignore` ayarı bulunmadı).
- **Düzeltme:** Tüm consumer'larda ortak bir "channel supervisor": `ChannelShutdownAsync` → linked CTS iptali → `ConnectAndConsumeAsync` yeniden (WorkerAutoDiscovery'deki mekanizmayı bir retry döngüsüne bağlamak); connection recovery sonrası bellekteki batch'i `Nack(requeue)` yerine sadece düşürüp yeniden teslimi beklemek; `throw` yerine backoff'lu retry.

#### H5. StatusTracker: zehirli batch tüketiciyi kalıcı olarak kilitler; prefetch=10 sabit → ~20 msg/s tavan
- **Dosya:** `StatusTrackerService.cs:213` (`BasicQosAsync(0, 10, false)`), `:441-450` (genel `Exception` → `RequeueMessages`), `:462-479`.
- **Neden:** Batch'te tek bir mesaj kalıcı DB hatasına yol açarsa (örn. `Result`/`Exception` alanı kolon limitini aşan payload, bozuk `StatusChangeLogs` JSON) batch sonsuza kadar bellekte requeue edilir, hiçbir tag ACK/NACK edilmez; 10 unacked mesajdan sonra broker teslimatı keser → tüm status işleme durur, alarm yok. Ayrıca `BatchSize=50` ile prefetch 10 arasındaki tutarsızlık batch'in hiçbir zaman "dolu" tetiklenmemesine, throughput'un `10 / BatchIntervalMs(500ms)` ≈ 20 msg/s/instance ile sınırlanmasına yol açıyor (her execution ≥ 2 mesaj + 60 s'de bir heartbeat).
- **Düzeltme:** Prefetch'i `BatchSize`'a bağlamak (ör. `BatchSize*2`); requeue sayacı tutup N denemeden sonra mesajı `Nack(requeue:false)` ile DLX'e göndermek (status kuyruğuna DLX argümanı eklemek); batch içindeki sorunlu mesajı izole etmek için tek-tek fallback.

#### H6. `FailedOccurrenceHandler`: her hata `Nack(requeue:true)` + prefetch 1 → zehirli mesaj DLQ işlemeyi sonsuza kadar bloke eder; idempotent değil
- **Dosya:** `FailedOccurrenceHandler.cs:72-90, 229-240, 194-196`.
- **Neden:** `Guid.Parse` bozuk header'da fırlatır, DB geçici hatası, vb. → aynı mesaj anında yeniden teslim edilir, sonsuz döngü. Yeniden teslimde (ACK kaybı) `FailedOccurrences`'a duplicate satır yazılır (`OccurrenceId` üzerinde unique guard yok).
- **Düzeltme:** Parse hatalarında `requeue:false`; geçici hatalarda sınırlı retry sonra park kuyruğu; `FailedOccurrence` için `(OccurrenceId, RetryCount)` unique + upsert.

#### H7. `RabbitMQPublisher`: her publish'te yeni kanal + başarı yolunda koşulsuz 500 ms bekleme
- **Dosya:** `Services/RabbitMQ/RabbitMQPublisher.cs:43, 95-120`.
- **Alıntı:** `var completedTask = await Task.WhenAny(returnTcs.Task, Task.Delay(500, cancellationToken));`
- **Neden:** Publisher confirms açıkken `BasicPublishAsync` broker ack'ini bekler; AMQP'de `basic.return`, `basic.ack`'ten **önce** gelir, dolayısıyla confirm döndükten sonra return gelmeyecektir — 500 ms bekleme yalnızca gecikme ekler. 100 job'lık batch, `MaxDegreeOfParallelism=4` → en az 12.5 s dispatch süresi (polling 10 s!). Buna per-publish `CreateChannelAsync` (round-trip + kanal numarası tahsisi) ekleniyor. Aynı publisher WorkflowEngine ve retry yolunda da kullanılıyor.
- **Düzeltme:** Confirm sonrası `returnTcs.Task.IsCompleted` kontrolü yeterli (bekleme yok); dispatch döngüsü için tek bir confirm'li kanalı (ya da küçük bir kanal havuzunu) yeniden kullanmak; `BasicReturnAsync` handler'ını kanal başına bir kez bağlayıp `CorrelationId` ile eşleştirmek.

#### H8. Circuit breaker fallback'leri durum değiştiren işlemlerde sessizce "başarılı gibi" dönüyor → publish sonrası Redis hıçkırığı duplicate dispatch üretir
- **Dosya:** `Services/Redis/RedisCircuitBreaker.cs:137-182`, `RedisSchedulerService.cs:286-309` (`UpdateScheduleAsync` fallback `false`), `:231-252` (`RemoveFromScheduledSetAsync` fallback `false`), `JobDispatcherService.cs:943-991` (dönüş değeri kontrol edilmiyor).
- **Neden:** Tek bir `RedisTimeoutException` (devre kapalıyken bile) fallback'e düşer; `HandleRecurringJobAsync` `UpdateScheduleAsync`'in `false` döndüğünü umursamaz → score geçmişte kalır → sonraki poll aynı job'ı tekrar dispatch eder. One-time job için `RemoveFromScheduledSetAsync` başarısızsa aynı sonuç. Redis "kısa süreli yavaşlık" durumunda job'lar 2× çalışır.
- **Düzeltme:** Publish'i **Redis reschedule başarısından sonra** yapmak (ya da önce ZSET'i ileri alıp publish başarısızsa geri almak), fallback yerine hatayı fırlatan bir `ExecuteCriticalAsync` varyantı ve başarısızlıkta occurrence'ı `DispatchPending` ile işaretleyip retry etmek.

#### H9. `ExternalIdentityService`: IdP grup adı yerel role adıyla eşleşiyor → `SuperAdmin` grubu tam yetki verir
- **Dosya:** `Services/ExternalIdentityService.cs:226-263` (`EnsureRolesAsync`: `r => wanted.Contains(r.Name)` — provider filtresi yok).
- **Neden:** OIDC/LDAP tarafında `SuperAdmin` (veya seed edilmiş herhangi bir yerel rol adı) adlı grup oluşturabilen biri, Milvaion'da `Id=1` SuperAdmin rolünün izinlerini alır. Rol yaratma yetkisi IdP'de daha geniş bir kitleye açık olabilir.
- **Düzeltme:** Eşleşmeyi `(Name, Provider)` çiftine bağlamak, `Provider=Local` rolleri external eşleşmeden hariç tutmak, ya da açık bir grup→rol eşleme tablosu.

#### H10. LDAP filter/DN enjeksiyonu
- **Dosya:** `Services/LdapAuthenticator.cs:25-27, 76`.
- **Alıntı:** `var filter = string.Format(_options.UserSearchFilter ?? "(sAMAccountName={0})", username);`
- **Neden:** Kullanıcı adı RFC 4515 (`\2a \28 \29 \5c \00`) ve RFC 4514 kaçışları olmadan filtreye/DN'e gömülüyor. Bind kullanıcı adıyla yapıldığı için doğrudan kimlik atlatma zor; ancak `UserSearchFilter` OR içeren bir şablon ise (`(|(sAMAccountName={0})(mail={0}))`) `x)(cn=admin` gibi bir değerle **başka bir kullanıcının** attribute/grup üyeliği okunup rol eşleşmesine gidebilir (`response.Entries[0]`). (güven: orta – gerçekleşmesi dizin şemasına bağlı.)
- **Düzeltme:** Filter değerlerini kaçışlamak (`\` `*` `(` `)` NUL), DN'de RFC 4514 kaçışı; birden fazla entry dönerse reddetmek.

### 2.3 MEDIUM

#### M1. Lock owner olarak `Environment.MachineName`; kilit iki kez bırakılıyor
- **Dosya:** `JobDispatcherService.cs:450, 662, 927`, `RedisLockService.cs:246-285`.
- **Neden:** Aynı host'ta iki process (ya da aynı hostname'li container'lar) aynı owner kimliğine sahip → biri diğerinin kilidini serbest bırakabilir (`ReleaseLocksBulk` owner eşleşince siler). Ayrıca per-job `finally` release'ten sonra `ReleaseLocksBulkAsync` tekrar çağrılıyor; bulk script "anahtar yok" durumunu da `released++` sayıyor → "Bulk lock release: 100/100" logu yanıltıcı, per-job release "does not own the lock" uyarıları üretir. Öneri: owner = `{MachineName}:{ProcessId}:{Guid}`; tek release yolu.

#### M2. `ZombieOccurrenceDetectorService`: her instance'ta koordinasyonsuz çalışıyor; sınırsız sorgu; per-occurrence sıralı Redis
- **Dosya:** `ZombieOccurrenceDetectorService.cs:93-167, 218-292, 297-364`.
- **Neden:** Named lock yok → N instance aynı zombiyi bulur, `UpdateStatusCountersAsync(Queued→Unknown)` N kez çalışır, N alert. `DetectZombieQueuedAsync` **tüm** `Queued` satırları çekiyor (SQL'de `CreatedAt < now - minTimeout` filtresi yok) ve her zombie için ayrı `MarkJobAsCompletedAsync`/`GetWorkerAsync`. `DetectLostRunningAsync` job-özel `ZombieTimeoutMinutes`'ı yok sayıp global eşik kullanıyor; heartbeat göndermeyen (SDK'da `JobHeartbeatIntervalSeconds<=0`) uzun job'lar yanlış `Unknown` olur. `_finalStatuses` (StatusTracker) `Unknown`/`Skipped`'ı final saymıyor, `IsFinalStatus()` sayıyor — tutarsız.
- **Düzeltme:** Named lock; SQL'de zaman filtresi + `Take`; Redis çağrılarını bulk (`RemoveRunningJobsAsync`); koşullu `UPDATE … WHERE Status=Running AND LastHeartbeat<…`.

#### M3. `JobMisfireMonitorService` yanlış pozitifler ve yapılandırma dışı anahtarlar
- **Dosya:** `JobMisfireMonitorService.cs:84-131`.
- **Neden:** Dispatcher tasarımı gereği `Skip` policy'li koşan job, kapasitesi dolu/aktif olmayan worker'a bağlı job ZSET'te geçmiş score ile bekler → her slot için "misfired" alarmı (60 s sonra). Anahtar `milvaion:misfire-alert:` sabit prefix'li ve `_redis.GetDatabase()` (DB 0), `RedisOptions.KeyPrefix/Database` yok sayılıyor. Aynı sorun `ApiKeyExpiryMonitorService.cs:96`.
- **Düzeltme:** Alarmdan önce nedeni sınıflandırmak (worker inaktif / kapasite / running-skip) ya da dispatcher'ın "skip" kararını Redis'e not düşmesi; anahtarları `RedisOptions` üzerinden üretmek.

#### M4. İki ayrı Redis multiplexer'ı, farklı `Database` seçimi, DI'da senkron connect, connection string log'u
- **Dosya:** `InfraServiceCollectionExtensions.cs:362-378`, `Services/Redis/RedisConnectionService.cs:22-27, 66`, `RedisWorkerService.cs:25`, `RedisStatsService.cs:23`, `JobMisfireMonitorService.cs:95`.
- **Neden:** `IConnectionMultiplexer` (`GetDatabase()` → DB 0) ve `RedisConnectionService` (`_options.Database`) paralel bağlantılar; `Database != 0` yapılandırmasında worker/stats anahtarları başka DB'ye gider. `ConnectionMultiplexer.Connect` DI factory'de bloklar. `RedisConnectionService.cs:66` connection string'i Information seviyesinde loglar — parola connection string içindeyse sızar (`AddRedisStorage` parolayı string'e ekliyor ama `RedisConnectionService` kendi `Password` set ediyor; yine de kullanıcı `ConnectionString` içine `password=` yazabilir). `Lazy<T>` `ExecutionAndPublication` modunda ilk istisnayı önbelleğe alır → ilk connect fırlatırsa süresiz `Fatal`. `RedisHealthCheck` yalnızca birini test eder.
- **Düzeltme:** Tek multiplexer + `GetDatabase(options.Database)`; `ConnectAsync`; log'da endpoint yalnızca.

#### M5. Cron/zaman semantiği: yalnızca UTC, misfire politikası yok, `Skip` policy "bitince hemen çalıştır" anlamına geliyor
- **Dosya:** `JobDispatcherService.cs:956-975, 746-760`, `Scheduling/StartupScheduleResolver.cs:53-60`, `WorkflowEngineService.cs:155-162`.
- **Neden:** `GetNextOccurrence(now, TimeZoneInfo.Utc)` — job başına timezone yok; "her gün 09:00 yerel" DST'de kayar. Redis flush/restart sonrası kaçırılan cron slotları sessizce atlanır (`FireNow`/`Skip`/`FireAll` seçimi yok) — kabul edilebilir ama belgelenmeli. `Skip` policy'de job koşarken score ilerletilmediği için iş biter bitmez (cron slotu dışında) tekrar dispatch edilir ve her poll'da Information seviyesinde "skipped" logu üretir. Workflow cron'da `LastScheduledRunAt = now` (slot yerine) yazıldığı için kısa periyotlarda drift.
- **Düzeltme:** `ScheduledJob.TimeZoneId` + per-job `MisfirePolicy`; `Skip` için score'u bir sonraki slota çekmek; workflow'da `LastScheduledRunAt = nextOccurrence`.

#### M6. `LogCollectorService`: at-most-once, gözlemlenmeyen batch task, tüm batch'i pending'e atan FK yolu
- **Dosya:** `LogCollectorService.cs:75-88, 180-243, 249-399, 372-381`.
- **Neden:** Mesaj DB'ye yazılmadan ACK'leniyor (StatusTracker ile tutarsız) → crash'te log kaybı. `batchTask` içinde `try/catch` yok; `ProcessBatchAsync` → `_batchLock.WaitAsync(0, ct)` shutdown'da `ObjectDisposedException` fırlatabilir, task sessizce biter, kuyruk `_maxQueueSize`'a kadar dolup düşer. Tek bir occurrence'ı henüz olmayan log, 10k'ya kadar tüm batch'i 500 ms+ geciktirir. `logsByOccurrence` ölü; `_maxQueueSize` kontrolü batch-mesaj yolunda yok; `SignalR` publish `Task.Delay(5s)` timeout'u yalnızca bekler, iptal etmez.

#### M7. `ExternalJobTrackerService`: at-most-once, PK çakışması tüm batch'i düşürür, sıra dışı `Completed` kaybolur
- **Dosya:** `ExternalJobTrackerService.cs:199-251, 456-509, 511-580`.
- **Neden:** ACK, batch persist'ten önce. `Id = message.CorrelationId` ile `BulkInsert`; aynı `Starting` yeniden teslimde PK ihlali → `catch` → tüm batch kaybolur. Çoklu instance'ta `Completed`, `Starting`'den önce işlenirse "not found" ile sessizce düşer, occurrence ebediyen `Running` (zombie detector `Unknown` yapar). `UpdateStatusCountersAsync`/`TrackDurationAsync` per-occurrence sıralı.
- **Düzeltme:** Upsert (`ON CONFLICT DO NOTHING`), `Completed` için "occurrence yoksa yarat" (Starting bilgisiyle), ACK'yi persist sonrasına almak.

#### M8. `WorkerAutoDiscoveryService`: graceful shutdown temizliği no-op; otomatik job "reassign"
- **Dosya:** `WorkerAutoDiscoveryService.cs:327-354`, `RedisSchedulerService.cs:43-125`, `WorkerAutoDiscoveryService.cs:245-306`.
- **Neden:** `RemoveAllRunningJobsForWorkerAsync(heartbeat.InstanceId)` — parametre `workerId` bekliyor; `running_jobs_by_worker:*` seti API'de hiç yazılmıyor (yalnızca `MilvaionMaintenanceWorker` temizliyor), fallback ise cached job'ın `WorkerId`'sini `InstanceId` ile karşılaştırıyor → hiç eşleşmez. `SyncRoutingPatternsAsync`, kayıt olan worker'ın job type'ı için "eski worker Redis'te yoksa" **tüm job'ların `WorkerId`'sini** değiştiriyor — worker'ın 2 dk heartbeat TTL'i dolmuşsa (geçici ağ kesintisi) job sahipliği sessizce el değiştirir. `worker.Metadata.HeartbeatInterval` 0 gelirse tüm instance'lar anında "dead" sayılır (`HeartbeatInterval*3 = 0`).

#### M9. Graceful shutdown sıralaması: semaphore'lar `base.StopAsync` öncesi dispose ediliyor; bekleyen Redis işleri kayboluyor
- **Dosya:** `StatusTrackerService.cs:1046-1069`, `LogCollectorService.cs:516-539`, `WorkerAutoDiscoveryService.cs:455-483`, `FailedOccurrenceHandler.cs:322-334`.
- **Neden:** Consumer handler'ları hâlâ çalışırken `_channelLock.Dispose()` → `SafeAckAsync` içinde `ObjectDisposedException` (catch bloklarında tekrar semaphore kullanımı → ikinci istisna → "Unhandled exception in log consumer" Fatal logları). StatusTracker `_pendingRedisCompletions` kuyruğu shutdown'da boşaltılmıyor → `running_jobs` SET'inde bayat job id'leri, `Skip` policy'li job'lar ZombieDetector reconcile'ına (10 dk grace) kadar bloke. `ExternalJobTrackerService` doğru sırayı (önce `base.StopAsync`) uyguluyor — diğerleri de öyle olmalı.

#### M10. `MemoryTrackedBackgroundService`: process-geneli ölçüm servis başına "leak" alarmı üretiyor ve zorla GC yapıyor
- **Dosya:** `BackgroundServices/Base/MemoryTrackedBackgroundService.cs:143-179, 188-203, 208`.
- **Neden:** `GC.GetTotalMemory(false)` tüm process'e ait; 1 saat sonra `LeakDetectionThresholdBytes` aşılınca **her** servis aynı anda `MemoryLeakDetected` Critical alert'i üretir (her `CheckIntervalSeconds`'ta tekrar). `GC.Collect(Aggressive, blocking, compacting)` bir background loop'tan tetiklenince API isteklerini duraklatır. Thread-allocation metriği de `Task.Run` ile başka thread'e geçince baseline sıfırlanıyor.
- **Düzeltme:** Alert'i process seviyesinde tek bir monitöre almak, forced GC'yi kaldırmak (`GCMemoryInfo` ile yalnızca raporlamak).

#### M11. `DatabaseMigrator` / `MigrationHostedService`: eşzamanlı instance başlangıcında idempotent olmayan seed; root parola stdout'ta; prod guard'sız developer uçları
- **Dosya:** `Persistence/DatabaseMigrator.cs:153-161, 356-429`, `Services/DeveloperService.cs:150-165, 84-121, 224-264`, `Api/Migrations/MigrationHostedService.cs`.
- **Neden:** İki pod aynı anda `InitDatabaseAsync` çalıştırırsa ikisi de `migrationLog == null` görür; seed çakışır, biri `Response.Error("Already initialized!")` ile hatayı maskeler (gerçek istisna `Console.WriteLine`). Seed adımları tek transaction'da değil (kısmi başarı). Rastgele root parolası `Console.WriteLine` ile container log'una yazılıyor. `DeveloperService.InitDatabaseAsync` ve `ResetUIRelatedDataAsync` (tüm menü/sayfa verisini siler) `IsCurrentEnvProduction` guard'ından yoksun (controller'da yalnızca `[Auth]`; global policy API katmanına ait). `SeedDevelopmentDataAsync` gerçek kişisel e-posta/kullanıcı adı ve `"string"` parolalar içeriyor. `ImportExistingDataAsync` döngü gövdesi boş (ölü).
- **Düzeltme:** Seed'i `pg_advisory_xact_lock` + transaction altında; parolayı yalnızca ilk kurulum çıktısı olarak ayrı kanala; dev uçlarını `#if DEBUG`/environment guard'ı + SuperAdmin izni.

#### M12. `RedisStatsService.SyncCountersFromDatabaseAsync` istatistik tahminleri nadir status'ları sıfırlıyor; `Skipped` sayılmıyor
- **Dosya:** `RedisStatsService.cs:231-359, 454-464`.
- **Neden:** `pg_stats.most_common_vals` yalnızca MCV listesindeki status'ları içerir; `Running`/`Queued` gibi anlık az sayıda satır MCV'de yoksa `0` yazılır, ardından floor'lu decrement'ler sayaçları kalıcı olarak düşük tutar. `Skipped` için anahtar yok (workflow skip'leri `Queued` sayacından düşülmez). `Completed = Total − diğerleri` formülünde Skipped dahil olmadığından şişer. Her 30 dk'da bir drift "düzeltmesi" aslında yeni drift yaratabilir.
- **Düzeltme:** Aktif status'lar (`Queued`, `Running`) için exact `COUNT(*) … WHERE Status IN (0,1)` (indeksli, küçük), geçmiş status'lar için tahmin.

#### M13. `ConditionExpression` / `EvaluateClause` fail-open
- **Dosya:** `Workflows/ConditionExpression.cs:52-75`, `WorkflowEngineService.cs:690-714, 781-782, 808-814`.
- **Neden:** Bozuk ifade, bilinmeyen clause, tanınmayan operatör → `true`. Bir yazım hatası koşul düğümünü sürekli "true" dalına yönlendirir; docs'ta "broken condition should not halt a pipeline" deniyor ama sessizce **yanlış** dalı çalıştırmak daha kötü. Bir enjeksiyon yüzeyi yok (özel parser), ancak `JsonNode.Parse(depOcc.Result)` derinlik sınırı yok (worker `Result` alanı) — düşük risk.
- **Düzeltme:** Parse hatasını yaratım anında (Application validate) reddetmek; runtime'da hata → run `Failed` + `Error`.

#### M14. Alerting: e-posta kanalı hatayı yutup "başarılı" dönüyor; `SmtpClient` obsolete; HTML enjeksiyonu; singleton'da yakalanmış `HttpClient`
- **Dosya:** `Channels/EmailAlertChannel.cs:47-58, 103-104`, `Channels/SlackAlertChannel.cs:22`, `Channels/TeamsAlertChannel.cs:21`, `Channels/GoogleChatAlertChannel.cs:22`, `Services/Monitoring/RabbitMQManagementClient.cs:98-107`.
- **Neden:** `SendMailAsync` istisnası loglanıp `ChannelResult.Successful` dönüyor → AlertNotifier "bir kanal başarılı" sayar. `payload.Title` (job display name'den geliyor) `<h2>` içine kaçışsız yazılıyor. `IHttpClientFactory.CreateClient` singleton ctor'da bir kez çağrılıyor → handler rotasyonu devre dışı (DNS değişiklikleri). Management API sabit `http://` + Basic auth (parola düz metin). Retry/backoff yok (10 s timeout var).

#### M15. Health check'ler canlılığı yansıtmıyor
- **Dosya:** `HealthChecks/*.cs`.
- **Neden:** RabbitMQ check yalnızca `connection.IsOpen`; StatusTracker/LogCollector consumer'ı ölmüş veya 10 denemeden sonra "disabled until restart" olmuş olsa da "Healthy". Redis check circuit breaker durumunu ve ikinci multiplexer'ı görmüyor. Dispatcher "degraded mode"/emergency-stop durumu health'e yansımıyor. Readiness/liveness ayrımı yok.
- **Düzeltme:** Her background service için "son başarılı iterasyon zamanı" ve consumer-tag durumu yayınlayan bir `IHealthCheck`; `IRedisCircuitBreaker.State` Open ise `Degraded`.

#### M16. `AccountManager`: ham access/refresh token'ları DB'de saklıyor; 20 yıllık CI token'ı
- **Dosya:** `Services/AccountManager.cs:66-76, 87-99`.
- **Neden:** `UserSession.AccessToken/RefreshToken` düz metin, `UserSessionHistory`'ye de kopyalanıyor → DB sızıntısında geçerli oturumlar. `GenerateToken` varsayılan `AddYears(20)`; iptal mekanizması görülmedi (API kapsamı). `RefreshTokenExpiresIn = ExpirationMinute * 5760` yorumu "24 hours" — yalnızca `ExpirationMinute=15` için doğru.
- **Düzeltme:** Token hash'ini (SHA-256) saklamak; CI token'larını `MilvaionApiKey` ile birleştirmek.

#### M17. `RabbitMQConnectionFactory`: shutdown/recovery penceresinde `CreateChannelAsync` fırlatır; ikinci `InitializeAsync` bağlantı sızdırır
- **Dosya:** `RabbitMQConnectionFactory.cs:180-196, 208-235, 49-71`.
- **Neden:** `OnConnectionShutdownAsync` `_initialized=false` yapar; recovery tamamlanana kadar tüm publish/consume `InvalidOperationException` (dispatcher döngüsü bunu "consecutive failure" sayar, 5'te degraded moda geçip Critical alert atar). Bu arada biri `InitializeAsync` çağırırsa `_connection` yeni bir connection ile ezilir, eskisi (auto-recovery'li) sızar.

#### M18. `LookupService.GetLookupsAsync` dinamik entity erişimi (güven: düşük)
- **Dosya:** `Services/LookupService.cs:28-33`.
- **Neden:** `_dbContext.GetLookupsAsync(request)` (Milvasoft) entity adını istekten alıyor; allowlist Application/Api katmanında doğrulanmadıysa `Users`/`UserSessions` gibi hassas entity'ler lookup ile okunabilir. Kütüphane davranışı bu incelemede doğrulanamadı; API katmanı incelemesinde `LookupRequest` validasyonu kontrol edilmeli.

### 2.4 LOW

- **L1.** `JobDispatcherService.cs:348` — istisna türü yerine `ex.Message.Contains("Circuit breaker") || ex.Message.Contains("Redis")` ile filtreleme; `RedisCircuitBreakerOpenException` tipi zaten var.
- **L2.** `JobDispatcherService.cs:1262` — `occurrenceExists` hesaplanıp kullanılmıyor; `WaitForDatabaseReadyAsync` `AnyAsync` ile tablo taraması yapıyor (küçük ama gereksiz).
- **L3.** `JobDispatcherService.cs:1137-1144` / `:1077` — `Task.Run` ile fire-and-forget, `StartAsync`'in `cancellationToken`'ı ile; shutdown'da iptal edilmez, hata gözlemlenmez.
- **L4.** `Services/Redis/RedisCancellationService.cs` — DI'a kayıtlı ama hiçbir yerden çağrılmıyor; aynı kanala `JobCancellationService`'ten farklı formatta (düz Guid vs JSON) mesaj yayınlar; ölü/çelişkili kod. `RedisWorkerService.DetectZombieWorkersAsync` stub (`[]`).
- **L5.** `Services/AdminService.cs` — `OccurrenceGrowthRawDto`, `LargeOccurrenceRawDto` ölü; `[Cache]` attribute'lu metotlar sınıf içinden çağrıldığı için interceptor devreye girmez (self-invocation).
- **L6.** `Services/PermissionManager.cs:97-98` — `PermissionCatalog.Permissions.Add` statik listeye `Clear` olmadan ekler; her `MigratePermissionsAsync` çağrısı duplicate üretir (`MigrationHostedService` de aynı listeyi dolduruyor).
- **L7.** `AlertNotifier`, `*AlertChannel`, `NotificationService` — `Lazy<IMilvaLogger>`: `IMilvaLogger` Scoped, `MilvaionLazy<T>` root provider'dan çözer; `ValidateScopes=true` (Development) ortamda logger çözümü fırlatır ve alert hataları sessizce kaybolur.
- **L8.** `RedisCircuitBreaker` — HalfOpen'da eşzamanlı tüm istekler geçer (tek probe yok); istatistik reset'i her saat tüm sayaçları sıfırlar.
- **L9.** `StatusTrackerService.cs:603-604` — `occurrence.WorkerId = message.WorkerId` null gelse bile ezer; `SafeNackAsync` `requeue:false` ve status kuyruğunda DLX yok → deserialize edilemeyen mesaj sessizce çöpe.
- **L10.** `RabbitMQPublisher.cs:46` — tüm `ScheduledJob` entity'si (JobData, AutoDisableSettings, CreatorUserName…) mesaj gövdesine seri hale getiriliyor; kontrat SDK DTO'su olmalı. `Priority = 0` sabit.
- **L11.** `WorkflowEngineService.cs:64-82` — 30 deneme sonunda DB hâlâ hazır değilse sessizce devam eder; `catch` gövdesi istisnayı loglamaz.
- **L12.** `Services/Redis/RedisSchedulerService.cs:822-824` — `DateTime.Parse(...)` kültüre bağlı (`"O"` ile yazılıp `Parse` ile okunuyor; `DateTimeStyles.RoundtripKind` yok → `Kind=Local` dönebilir, `GetUnixTimestamp`'te `ToUniversalTime` ile kayma riski; benzer `RedisWorkerService.cs:283, 344`).
- **L13.** `InfraServiceCollectionExtensions.cs:97-102` — `RedisStatSyncJob` `TimeZoneInfo.Local` ile; container TZ'ye bağımlı (zararsız ama tutarsız).
- **L14.** `JobDispatcherService.cs:753` — `Skip` policy'de koşan her job için her poll'da Information log; yüksek hacimde gürültü.
- **L15.** `GlobalSuppressions.cs` — `CA1869` bastırılmış; `DatabaseMigrator.SeedUIRelatedDataAsync` her çağrıda yeni `JsonSerializerOptions`.

---

## 3. Mimari İyileştirme Önerileri

1. **Dispatch'i atomik claim'e çevirin (C1, H8, M1).** Tek bir Lua script: `ZRANGEBYSCORE key -inf now LIMIT 0 N` → her üye için `ZADD key nextScore member` (cron) ya da `ZREM` (one-time) → claim edilen id listesi. Böylece per-job lock, "reschedule after publish" penceresi ve owner sorunları ortadan kalkar. Publish başarısızsa score'u geri alan (`ZADD` eski score) telafi adımı + `DispatchPending` occurrence status'u ile retry.

2. **Outbox/"persist-then-publish" sırasını netleştirin (C2, H8).** Şu an sıra: lock → DB insert → RabbitMQ publish → Redis reschedule. Doğru sıra: claim (Redis, atomik) → DB insert (status `Queued`, `DispatchAttempt`) → publish (confirm) → başarısızsa DB'de `NextDispatchRetryAt` (status hâlâ `Queued`) ve Redis'te retry-score. Bir `DispatchOutbox` tablosu, restart sırasında kaybolan "insert edildi ama publish edilmedi" occurrence'ları da kapatır.

3. **Concurrency token + koşullu güncellemeler (C4).** `JobOccurrence` için `xmin` concurrency token; ancak BulkExtensions token'ı uygulamadığı için status geçişlerini `ExecuteUpdateAsync(... WHERE Status IN (...))` ile yapın ve etkilenen satır sayısını kontrol edin. Final→non-final guard'ı DB'de olsun, bellekte değil.

4. **Çoklu instance için açık rol ayrımı (C1, C3, H2, M2).** Ya `IDistributedLeaderElection` (Redis named lock + heartbeat extend) ile "singleton" servisleri (Dispatcher, WorkflowEngine, ZombieDetector, MisfireMonitor, startup recovery) tek instance'a bağlayın ya da her birini atomik claim'lerle tamamen çok-yazıcı güvenli yapın; mevcut durumda ikisi de değil. Dokümanları koda göre güncelleyin. Emergency stop, dispatcher pause vb. runtime kontrolleri Redis'e taşıyın (SettingsProvider pub/sub deseni hazır).

5. **RabbitMQ consumer altyapısını ortaklaştırın (H4, H5, H6, M9).** `BatchingConsumerBase<TMessage>`: kanal ölümünde otomatik yeniden bağlanma, prefetch = f(batchSize), ACK/NACK stratejisi (persist sonrası ACK, N başarısız denemeden sonra `requeue:false` + DLX), shutdown'da doğru sıralama (önce `base.StopAsync`, sonra kalan batch, en son semaphore dispose), ölçülebilir "son başarılı batch" zamanı → health check.

6. **Redis erişimini tekilleştirin (M4, M3).** Tek `IConnectionMultiplexer`, `IDatabase` `options.Database` ile, tüm anahtarlar `RedisOptions.KeyPrefix` üzerinden; `RedisConnectionService`'i multiplexer üzerine ince bir sarmalayıcıya indirin. Circuit breaker'da "kritik" ve "best-effort" operasyon ayrımı: kritik olanlar fallback yerine fırlatır.

7. **Zamanlama semantiğini ürünleştirin (M5).** Job başına `TimeZoneId`, `MisfirePolicy` (`FireNow`/`Skip`/`FireAll`), `Skip` policy'de bir sonraki slota erteleme; bu semantikler misfire monitörünün de yanlış pozitiflerini keser.

8. **Servis-lokator ve tanrı sınıflarını sadeleştirin.** `JobDispatcherService` (1.5k satır) → `DueJobLoader`, `CapacityGate`, `OccurrencePersister`, `Publisher`, `RetryScheduler`, `StartupRecovery` gibi bileşenlere; `AdminService`/`ExportService`/`WorkflowEngineService`'teki `IServiceProvider` kullanımı yerine açık bağımlılıklar. `RedisWorkerService`'te tek anahtar şeması ve ölü metodların kaldırılması.

9. **Gözlemlenebilirlik.** `BackgroundServiceMetrics` iyi bir başlangıç; buna "consumer alive", "last successful iteration", "circuit state", "in-memory requeue count", "pending redis completions" gauge'ları ekleyin ve health check'lere bağlayın. Bellek izleyicisini process seviyesine indirip forced GC'yi kaldırın.

10. **Güvenlik sertleştirme (H9, H10, M11, M14, M16).** Provider-scoped rol eşleme; LDAP kaçışlama; token hash'leme; dev/seed uçlarının prod'da derlenmemesi; alert kanallarında HTML kaçışlama ve gerçek başarısızlık raporu; management API için `https` seçeneği.

---

# BÖLÜM C — API & Güvenlik


Kapsam: `src/Milvaion.Api` (AppStartup, Controllers, Middlewares, Hubs, Mcp, Services, Utils, appsettings, Dockerfile'lar, launchSettings, Migrations), `src/Milvaion.Api.SqlServer`, `docker-compose.yml`, `keycloak/realm-milvaion.json` ve API yüzeyini değerlendirmek için gereken `Milvaion.Application/Utils` + `Milvaion.Infrastructure/Services` (AccountManager, PermissionManager, ExternalIdentityService, LdapAuthenticator, DeveloperService, DatabaseMigrator) kodu.

Yöntem: Tüm controller'lar, startup dosyaları, middleware, hub, MCP tool sınıfları ve auth altyapısı satır satır okundu; `AllowAnonymous`, `Auth`, `Authorize`, secret/password, CORS, HTTPS, MapHub/MapMcp gibi kalıplar repo genelinde grep'lendi. Migration'lar yüzeysel tarandı. Kod salt-okunur incelendi, değişiklik yapılmadı.

---

## 1. Genel Bakış ve Kalite Değerlendirmesi

**Mimari:** Controller'lar büyük ölçüde ince (thin) — `IMediator.Send` ile Application katmanına delege ediyor, `CancellationToken` her yerde iletiliyor, response envelope (`Response<T>`, `ListResponse<T>`) tutarlı. Yetkilendirme modeli net: `[Auth]` (AuthorizeAttribute türevi) permission adlarını `ClaimTypes.Role` olarak taşıyor ve her endpoint'e `PermissionCatalog.*` sabitiyle ince taneli izin bağlanmış. MCP tool'ları `McpPermissionGuard.Require(...)` ile aynı izin setine bağlanmış; hiçbir MCP tool'u koruma dışında bırakılmamış. API key şeması ayrı bir `AuthenticationHandler` olarak temiz yazılmış, cache invalidation ve LastUsedAt throttling düşünülmüş. OIDC/LDAP entegrasyonunda "kimlik IdP'de, izinler Milvaion'da" ayrımı doğru bir tasarım kararı.

**Ancak** üretim (production) güvenlik duruşu ciddi biçimde zayıf. Sorunların büyük kısmı tek bir kök nedene bağlanıyor: **"production" kararı `ASPNETCORE_ENVIRONMENT` yerine özel bir `MILVA_ENV == "prod"` değişkenine bağlı** ve bu değişken ne `Dockerfile`'da ne de `docker-compose.yml`'de `prod` olarak ayarlanıyor. Sonuç olarak resmi Docker imajıyla ayağa kalkan her kurulumda:

- Kimlik doğrulaması olmayan `DeveloperController` (DB reset/seed) açık,
- Scalar/OpenAPI arayüzü açık ve içine **1 yıllık SuperAdmin JWT gömülü**,
- Tüm gizli anahtarlar (JWT imza anahtarı, API key HMAC secret'ı, DB şifresi) repo'daki varsayılan değerlerle çalışıyor.

Bu üçü bir araya geldiğinde, internete açık bir Milvaion kurulumu için **kimlik doğrulamasız tam sistem ele geçirme** senaryosu gerçekçi.

Kod kalitesi açısından: `.Result/.Wait/async void` yok, `DateTime.Now` sadece bir yerde, isimlendirme birkaç kopyala-yapıştır hatası dışında tutarlı. Dead code (`ApiAuthAttribute`, `IAccountManager.GenerateToken`, boş migration dosyası, boş `Milvaion.Api.SqlServer` projesi) ve tamamlanmamış özellikler (`ExportsController` her zaman 403 döner) mevcut.

**Özet skor tablosu**

| Alan | Durum |
|---|---|
| Endpoint bazlı yetkilendirme | İyi (Developer/Hub hariç) |
| Secret yönetimi | Zayıf — tüm secret'lar repo'da, rotasyon yok |
| Üretim/geliştirme ayrımı | Kırık (`MILVA_ENV`) |
| Token yaşam döngüsü | Orta — logout/rol değişimi access token'ı geçersiz kılmıyor |
| SignalR | Zayıf — auth yok, `Clients.All` broadcast |
| Hata yönetimi | Orta — yanlış status code'lar, header'lar loglanıyor |
| Gözlemlenebilirlik | Bir Serilog filtresi neredeyse tüm framework loglarını siliyor |
| SQL Server desteği | Yok (boş proje, Postgres-only migration'lar) |

---

## 2. Bulgular

### 🔴 CRITICAL

#### C1. Scalar dokümantasyon sayfası herkese açık ve içine 1 yıllık SuperAdmin JWT gömülü
**Dosya:** `src/Milvaion.Api/AppStartup/ApplicationBuilderExtensions.cs:25-64`, `Program.cs:151`
```csharp
public static IApplicationBuilder UseScalarWithOpenApi(this WebApplication app)
{
    if (MilvaionExtensions.IsCurrentEnvProduction())   // MILVA_ENV == "prod"
        return app;
    ...
    options.AddHttpAuthentication("Bearer", auth => { auth.Token = GenerateTokenForUI(app); });
    ...
    static string GenerateTokenForUI(WebApplication app)
    {
        var roleClaim = new Claim(ClaimTypes.Role, PermissionCatalog.App.SuperAdmin);
        var userClaim = new Claim(ClaimTypes.Name, "rootuser");
        var accessToken = tokenManager.GenerateToken(expired: DateTime.UtcNow.AddYears(1), issuer: null, userClaim, roleClaim);
        return accessToken;
    }
}
```
**Neden:** `/api/documentation` endpoint'i anonim. `MILVA_ENV` `Dockerfile`'da hiç set edilmiyor, `docker-compose.yml`'de `dev`. Dolayısıyla varsayılan üretim imajında Scalar açık ve sayfanın konfigürasyonuna, uygulamanın **gerçek imza anahtarıyla imzalanmış, `App.SuperAdmin` rolüne sahip, 1 yıl geçerli** bir bearer token yazılıyor. Sayfayı açan herkes bu token'ı alıp tüm API'yi (kullanıcı oluşturma, job tetikleme, DB istatistikleri, MCP) SuperAdmin olarak kullanabilir. Token JWT'de session tablosuna bağlı olmadığı için iptal de edilemez (bkz. H5).
**Düzeltme:** Token gömmeyi tamamen kaldırın (geliştirici login'den token alsın). Scalar'ı `app.Environment.IsDevelopment()` ile koşullayın veya `[Auth]` arkasına alın. `MILVA_ENV` yerine `IHostEnvironment` kullanın.

#### C2. `DeveloperController` kimlik doğrulaması olmadan DB'yi silip yeniden oluşturabiliyor
**Dosya:** `src/Milvaion.Api/Controllers/DeveloperController.cs:15-67`, `src/Milvaion.Infrastructure/Services/DeveloperService.cs:41-56,153`
```csharp
public class DeveloperController(IDeveloperService developerService) : ControllerBase   // [Auth] YOK
{
    [HttpPost("database/reset")]  public Task<Response> MigrateAsync() => _developerService.ResetDatabaseAsync();
    [HttpPost("database/seed")]   public Task<Response> SeedDataAsync() => ...
    [HttpPost("database/seed/fake")] ...
    [HttpPost("database/init")]   public async Task<Response> InitDatabaseAsync() => await _developerService.InitDatabaseAsync();  // prod guard bile yok
    [HttpGet("export/productRelatedData")] ... [HttpGet("import/productRelatedData")] ...
```
**Neden:** Sınıfta ne `[Auth]` ne `[Authorize]` var; `FallbackPolicy` tanımlı değil, dolayısıyla tüm bu action'lar **anonim**. Tek koruma `MilvaionExtensions.IsCurrentEnvProduction()` (`MILVA_ENV == "prod"`), o da varsayılan dağıtımda `false`. `database/reset` → `DatabaseMigrator.ResetDatabaseAsync` veritabanını düşürüp yeniden yaratıyor (tam veri kaybı). `database/seed` `bugrakosen` / `viewer` kullanıcılarını **`"string"` şifresiyle** ve SuperAdmin rolüyle oluşturuyor → anonim saldırgan kendine SuperAdmin hesabı açabilir. `database/init` üzerinde prod guard'ı hiç yok ve `Response<string>` içinde **root şifresini** döndürüyor (startup'ta init başarısız olduysa).
**Düzeltme:** Controller'ı `#if DEBUG` veya `IHostEnvironment.IsDevelopment()` ile hiç register etmeyin (`ApplicationPart` konvansiyonuyla filtreleyin); ek olarak `[Auth(PermissionCatalog.App.SuperAdmin)]` koyun. Seed şifrelerini ortam değişkeninden alın.

#### C3. Tüm kriptografik secret'lar repo'da hardcoded ve varsayılan olarak kullanılıyor
**Dosya:** `src/Milvaion.Api/appsettings.json:3,22,247-262`, `appsettings.Development.json`, `docker-compose.yml:9,106,140`
```json
"DefaultConnectionString": "Host=postgres;...;Password=N4SQp.qW>6?xwWzg;...;Include Error Detail=true"
"ApiKey": { "Secret": "XCAP05H6LoKvbRRa/QkqLNMI7cOHguaRyHzyg7n5qEkGjQmtBhz4SzYh4Fqwjyi3KJHlSXKPwVu2+bXr6CtpgQ==", "Version": 1 }
"Token": { "TokenValidationParameters": { "ValidateIssuer": false, "ValidateAudience": false },
           "SecurityKeyType": 0, "SymmetricPublicKey": "9321acf6145bc0a5d573a835be473682992d17a17830de85a122c715633ae5e9" }
"FileOperations": { "Json": { "EncryptionKey": "w!z%C*F-JaNdRgUk" } }
```
docker-compose: `POSTGRES_PASSWORD: N4SQp.qW>6?xwWzg`, `MILVAION_ROOT_PASSWORD=admin`, `SEQ_FIRSTRUN_ADMINPASSWORD=ChangeMe123!`, Grafana `admin/admin`, RabbitMQ `guest/guest`, pgweb URL'sinde DB şifresi.
**Neden:** Açık kaynak bir projede JWT imza anahtarı (`SymmetricPublicKey`) herkes tarafından biliniyor; `ValidateIssuer/ValidateAudience=false` olduğu için anahtarı değiştirmemiş **her kurulum için** dışarıdan `App.SuperAdmin` rol claim'li JWT üretilebilir. Aynı şekilde `ApiKey.Secret` bilindiğinden, var olan herhangi bir API key kaydının id'si (`jti`) için sahte key imzalanabilir (izinler DB'den geldiği için o kaydın izinleriyle). Startup'ta "secret varsayılan değerde mi" kontrolü yok. `Include Error Detail=true` Postgres hata mesajlarında satır verisini sızdırır.
**Düzeltme:** appsettings'ten tüm secret'ları çıkarın, `ValidateOnStart` ile boş/varsayılan değerde başlatmayı reddedin, README'de zorunlu env var'ları belgeleyin, `docker-compose.yml`'de `.env` kullanın. `Include Error Detail`'i prod'da kapatın.

---

### 🟠 HIGH

#### H1. `App.SuperAdmin` bir Permission satırı — rol/API key düzenleme yetkisi olan herkes SuperAdmin olabilir
**Dosya:** `src/Milvaion.Application/Utils/PermissionManager/PermissionCatalog.cs:13-18`, `Features/Roles/UpdateRole/UpdateRoleCommandHandler.cs:37-45`, `Features/ApiKeys/CreateApiKey/CreateApiKeyCommandHandler.cs:26-28`, `Features/Users/UpdateUser/UpdateUserCommandHandler.cs:65-72`
```csharp
// PermissionCatalog: App.SuperAdmin diğer permission'larla aynı katalogda → DB'de Permissions tablosunda (Id=1)
// UpdateRoleCommandHandler: PermissionIdList hiçbir filtreden geçmiyor
var addedEntities = request.PermissionIdList.Value?.Distinct().Select(permissionId => new RolePermissionRelation { RoleId = request.Id, PermissionId = permissionId })
// CreateApiKeyCommandHandler: aynı şekilde
apiKey.ApiKeyPermissionRelations = request.PermissionIdList?.Distinct().Select(permissionId => new MilvaionApiKeyPermissionRelation { PermissionId = permissionId })
```
**Neden:** `RoleManagement.Update` yetkili bir kullanıcı kendi rolüne `PermissionId=1` (App.SuperAdmin) ekleyip tam yetki alır. `UserManagement.Update` yetkili biri kendine `RoleId=1` (SuperAdmin rolü) atayabilir. `ApiKeyManagement.Create` yetkili biri `App.SuperAdmin` izinli API key üretebilir; `ApiKeyStore` projeksiyonu `PermissionGroup + "." + Name` → `"App.SuperAdmin"` role claim'i verir ve `AuthAttribute` bunu kabul eder. Root kullanıcı (Id=1) ve SuperAdmin rolü (Id=1) için silme/değiştirme koruması yok (DB trigger'ları "DefaultDataCannotModify" için var ama kod tarafında handler'da kontrol yok; trigger kapsamı doğrulanamadı).
**Düzeltme:** `App.SuperAdmin`'i atanabilir permission listesinden çıkarın (yalnızca seed edilen rolde); handler'larda "SuperAdmin permission'ı/rolü yalnızca SuperAdmin atayabilir", "kendi rolünü yükseltemez", "Id=1 kayıtları değiştirilemez" kurallarını uygulayın. API key'lere SuperAdmin verilemesin.

#### H2. API key'ler `[Auth]` ile korunan **her** endpoint'te geçerli — ApiKeysController dahil
**Dosya:** `src/Milvaion.Api/AppStartup/ServiceCollectionExtensions.cs:58-65`, `Controllers/ApiKeysController.cs:19-27`, `Utils/ApiAuthAttribute.cs`
```csharp
var authenticationSchemes = oidc.Enabled ? [MultiBearer, ApiKey] : [JwtBearer, ApiKey];
services.AddAuthorizationBuilder().SetDefaultPolicy(new AuthorizationPolicyBuilder([.. authenticationSchemes]).RequireAuthenticatedUser().Build());
```
```csharp
/// These endpoints are deliberately reachable with an interactive session only. Managing api keys with an api key
/// would let a leaked key mint replacements for itself and survive revocation.
[Auth] public class ApiKeysController ...
```
**Neden:** Varsayılan policy ApiKey şemasını içerdiği için `[Auth]` her yerde API key kabul ediyor; `ApiKeysController` üzerindeki yorum yanlış — `ApiKeyManagement.*` izinli bir API key kendine yedek key üretebilir, revoke edebilir. Bunun için tasarlanmış `ApiAuthAttribute` (`Utils/ApiAuthAttribute.cs`) **hiçbir yerde kullanılmıyor** (dead code). Ayrıca API key principal'ı `ClaimTypes.Name = apiKey.Name` taşıdığı için `IsCurrentUser(userName)` kontrolleri API key adı bir kullanıcı adına eşitse (ör. "rootuser" adlı key) hesap endpoint'lerinde çakışabilir.
**Düzeltme:** `ApiKeysController` ve `AccountController`'a `[Authorize(AuthenticationSchemes = JwtBearer)]` gibi yalnızca-interaktif policy uygulayın; ya da default policy'den ApiKey'i çıkarıp `[ApiAuth]`'u gerçekten kullanın. API key identity'sine `ClaimTypes.Name` yerine ayrı bir claim verin.

#### H3. SignalR hub'ı kimlik doğrulamasız; tüm occurrence olayları `Clients.All`'a yayınlanıyor
**Dosya:** `src/Milvaion.Api/Hubs/JobsHub.cs:9-34`, `Program.cs:146`, `Services/SignalRJobOccurrenceEventPublisher.cs:121-128`
```csharp
public class JobsHub : Hub   // [Authorize] yok
app.MapHub<JobsHub>("/hubs/jobs");   // RequireAuthorization() yok
...
await _hubContext.Clients.All.SendAsync("OccurrenceUpdated", signal, cancellationToken);   // signal.Exception dahil
```
**Neden:** Herhangi bir istemci `/hubs/jobs`'a bağlanıp `SubscribeToOccurrence(<guid>)` çağırabilir ve job adı, worker id, durum ve **exception metni** dahil tüm occurrence olaylarını izin kontrolü olmadan alır. `OccurrenceLogAdded` ile log satırları da gruba akıyor. CORS `AllowAll` olduğundan bu tarayıcıdan da yapılabilir. Ayrıca statik `_activeGroups` sözlüğü çok-instance (load-balanced) kurulumda tutarsız; `PublishOccurrenceCreatedAsync` her occurrence için `Task.Run` açıyor (BatchSize=1000 → thread-pool patlaması) ve 3 sn timeout sonrası task'lar iptal edilmiyor.
**Düzeltme:** `[Authorize]` + `MapHub(...).RequireAuthorization()`; `SubscribeToOccurrence` içinde `ScheduledJobManagement.Detail` kontrolü; `Clients.All` yerine izinli kullanıcı grubu; `Task.Run` yerine doğrudan await/`Parallel.ForEachAsync`. Çoklu instance için Redis backplane.

#### H4. `LookupsController` — izin gerektirmeden `User` ve `JobOccurrence` entity'leri dinamik sorgulanabiliyor *(güven: orta)*
**Dosya:** `src/Milvaion.Api/Controllers/LookupsController.cs:28-35`, `appsettings.json:196-200`
```csharp
[Auth] public class LookupsController ...
[HttpPatch] [Auth] public async Task<ListResponse<object>> GetLookupsAsync(LookupRequest request)
```
```json
"DynamicFetch": { "EntityAssemblyName": "Milvaion.Domain",
  "AllowedEntityNamesForLookup": [ "User", "Role", "ScheduledJob", "JobOccurrence" ], "MaxAllowedPropertyCountForLookup": 5 }
```
**Neden:** Endpoint yalnızca "authenticated" istiyor; herhangi bir permission'ı olmayan bir kullanıcı (ör. seed edilen `viewer`) `EntityName=User, RequestedPropertyNames=[UserName,Email,PasswordHash,...]` veya `JobOccurrence` (JobData/Exception/Result) isteyebilir. Milvasoft `GetLookupsAsync` uygulaması nuget cache'te olmadığından property whitelist'i olup olmadığı doğrulanamadı; ama `UserManagement.List` / `ScheduledJobManagement.List` kontrolünün burada olmadığı kesin.
**Düzeltme:** Entity başına permission map'i (`User→UserManagement.List` vb.) uygulayın; `PasswordHash`, `SecurityStamp` gibi kolonları dinamik seçime kapatın.

#### H5. Access token'lar session tablosuna bağlı değil — logout / rol değişimi / kullanıcı silme token'ı geçersiz kılmıyor
**Dosya:** `src/Milvaion.Infrastructure/Services/AccountManager.cs:26-72`, `Application/Features/Account/Logout/LogoutCommandHandler.cs`, `ServiceCollectionExtensions.cs:82-129`
```csharp
RefreshTokenExpiresIn = _identityOptions.Token.ExpirationMinute * 5760 // 24 hours   ← 90*5760 s = 6 gün
...
AccessToken = milvaToken.AccessToken, RefreshToken = milvaToken.RefreshToken,   // plaintext DB'de
```
**Neden:** `JwtBearer` şemasında `OnTokenValidated` ile session doğrulaması yok; logout yalnızca `UserSession` satırını siliyor, JWT 90 dk daha geçerli. Rol değişince sessions siliniyor ama eski token'daki role claim'leri yaşamaya devam ediyor. Session tablosunda access ve refresh token düz metin saklanıyor (DB sızıntısı = oturum ele geçirme). Refresh token karşılaştırması `==` (timing-safe değil, düşük risk). Yorumdaki "24 hours" ile kod (6 gün) uyuşmuyor. `IAccountManager.GenerateToken` 20 yıllık token üretiyor ve hiçbir yerde kullanılmıyor (tehlikeli dead code).
**Düzeltme:** Token'a `jti`/session id koyup `OnTokenValidated`'da session varlığını (Redis cache'li) doğrulayın; refresh token'ı hash'leyerek saklayın; `GenerateToken`'ı kaldırın; refresh süresini sabit ve doğru hesaplayın.

#### H6. OIDC/LDAP rol eşleme yalnızca **isim** üzerinden — IdP'de "SuperAdmin" adlı rol yerel SuperAdmin rolüne bağlanır
**Dosya:** `src/Milvaion.Infrastructure/Services/ExternalIdentityService.cs:226-262`, `appsettings.Development.json` (`RolePrefix: ""`)
```csharp
var existing = (await _roleRepository.GetAllAsync(condition: r => wanted.Contains(r.Name), ...)).ToList();
```
**Neden:** Provider filtresi yok; IdP'den gelen `roles` claim'i `SuperAdmin` içeriyorsa kullanıcı seed edilen `Role.Id=1` (`Name="SuperAdmin"`) ile eşleşir ve `App.SuperAdmin` izni alır. `RolePrefix` boşsa (dev config'de öyle) tüm IdP rolleri kabul edilir. Keycloak realm yöneticisi ≠ Milvaion yöneticisi olan kurumlarda bu, IdP tarafından yatay yetki yükseltmedir.
**Düzeltme:** Eşlemede `r.Provider == provider` şartı; yerel `SuperAdmin` rolünün dış kaynaklardan eşlenmesini açıkça yasaklayın; `RolePrefix` zorunlu kılın.

#### H7. LDAP: kullanıcı adı filtreye kaçışsız gömülüyor (LDAP injection), TLS varsayılan kapalı
**Dosya:** `src/Milvaion.Infrastructure/Services/LdapAuthenticator.cs:25-28,55,72-78`
```csharp
var userBindDn = string.Format(_options.UserDnFormat, username);
var filter = string.Format(_options.UserSearchFilter ?? "(sAMAccountName={0})", username);
connection.SessionOptions.SecureSocketLayer = _options.UseSsl;   // appsettings.Development: false, AuthType.Basic
```
**Neden:** `username` içindeki `*()\|&` karakterleri kaçışlanmıyor → servis hesabı (BindDn) ile yapılan aramada filtre manipülasyonu / grup üyeliği sahteciliği (`memberOf` → Milvaion rolü). `UseSsl=false` + Basic bind → şifreler ağda düz metin. `appsettings.Development.json` içinde `BindPassword: "AdminP@ss123"` hardcoded.
**Düzeltme:** RFC 4515 kaçışlama (`\2a`, `\28` ...), DN için RFC 4514; `UseSsl`/StartTLS varsayılan açık; `BindPassword` env var.

---

### 🟡 MEDIUM

#### M1. Serilog filtresi `SourceContext` + `EventId` taşıyan **tüm** olayları siliyor
**Dosya:** `src/Milvaion.Api/AppStartup/ServiceCollectionExtensions.cs:723-727`
```csharp
loggerConfig.Filter.ByExcluding(logEvent =>
    logEvent.Properties.TryGetValue("SourceContext", out var sourceContext)
    //&& sourceContext.ToString().Contains("JwtBearerHandler")
    && logEvent.Properties.TryGetValue("EventId", out var eventId)
    /*&& eventId.ToString().Contains("ProcessingMessageFailed")*/);
```
**Neden:** Koşullar yorum satırına alınınca filtre "EventId'si olan her ILogger olayı" haline gelmiş: Kestrel, EF Core, JwtBearer, HealthCheck, Hosting uyarı/hataları prod'da görünmez. Güvenlik olaylarının (başarısız auth) izlenemez olması operasyonel risk.
**Düzeltme:** Filtreyi kaldırın veya `MinimumLevel.Override` ile hedefli susturun.

#### M2. `ExceptionMiddleware` — yanlış status code'lar, tüm request header'ları (Authorization, X-ApiKey) loglanıyor
**Dosya:** `src/Milvaion.Api/Middlewares/ExceptionMiddleware.cs:156-168,224-247`
```csharp
await RewriteResponseAsync(context, ex.Message, StatusCodes.Status200OK, ...);       // iş hatası → 200
await RewriteResponseAsync(context, MessageKey.UnhandledException, (int)HttpStatusCode.BadRequest);  // beklenmeyen hata → 400
...
Headers = context.Request.Headers,   // Authorization + X-ApiKey dahil, Seq'e gidiyor
```
**Neden:** Handled olmayan exception 500 yerine 400 dönüyor (izleme/alerting yanıltıcı, Prometheus 5xx sayaçları boş). `PostgresException` → 200 "Warning". `RequestInfo.Headers` ile bearer token ve API key Seq'e düşüyor. `MilvaUserFriendlyException.Message` ham olarak istemciye gidiyor.
**Düzeltme:** 500 kullanın; header'ları allowlist'le (`User-Agent`, `Content-Type`...) loglayın; `ProblemDetails`'e geçmeyi değerlendirin.

#### M3. `ExportsController` her zaman 403 — `CurrentUserPermissionsKey` hiç set edilmiyor
**Dosya:** `src/Milvaion.Application/Utils/Extensions/HttpExtensions.cs:88-101`, `Infrastructure/Services/ExportService.cs:40`
```csharp
var currentUserPermissonExists = httpContext.Items.TryGetValue(GlobalConstant.CurrentUserPermissionsKey, out var contextItem);
if (!currentUserPermissonExists) httpContext.Response.ThrowWithForbidden();
```
**Neden:** Repo genelinde `CurrentUserPermissionsKey`'e yazan kod yok (grep: yalnızca okuyan yer). Excel export özelliği SuperAdmin için bile çalışmaz. (Güven: yüksek; runtime doğrulaması önerilir.)
**Düzeltme:** İzinleri `HttpContext.User` role claim'lerinden okuyun (`User.IsInRole`) — Items'a bağımlılığı kaldırın.

#### M4. Prometheus `/api/metrics` ve `/api/health/ready` kimlik doğrulamasız iç bilgi sızdırıyor
**Dosya:** `ApplicationBuilderExtensions.cs:135-142`, `Controllers/HealthCheckController.cs:20,41-65`, `Infrastructure/HealthChecks/RabbitMQHealthCheck.cs:26-52`
**Neden:** Metrics endpoint'i route şablonları, DB/HTTP client ölçümleri, background service bellek verilerini anonim veriyor. `/ready` cevabı RabbitMQ host/port ve exception mesajlarını (`["Error"] = ex.Message`) içeriyor.
**Düzeltme:** Metrics'i ayrı port/IP allowlist veya basic auth arkasına alın; `/ready`'de yalnızca `Status` döndürün, detayları `[Auth]` altında ayrı endpoint yapın.

#### M5. Root kullanıcı `LockoutEnabled=false`, rate limiting yok, lockout mesajı kullanıcı varlığını ifşa ediyor
**Dosya:** `Infrastructure/Persistence/DatabaseMigrator.cs:140`, `Application/Features/Account/Login/LoginCommandHandler.cs:115-140`, `Program.cs` (AddRateLimiter yok)
**Neden:** `rootuser` için brute-force sınırsız; `LoginCommandHandler` var olmayan kullanıcıya `Unauthorized`, var olana "N deneme kaldı / kilitli" mesajı döndüğü için kullanıcı adı enumerasyonu mümkün. `MILVAION_ROOT_PASSWORD` verilmezse şifre `Console.WriteLine` ile loglara basılıyor.
**Düzeltme:** `AddRateLimiter` (login/refresh için IP + kullanıcı adı bazlı), root için de lockout, tek tip hata mesajı.

#### M6. `X-Real-IP` başlığına koşulsuz güven, `ForwardedHeaders` middleware'i yok
**Dosya:** `Application/Utils/Extensions/HttpExtensions.cs:106-124`, `Program.cs`
```csharp
var realIpExists = httpContext.Request.Headers.TryGetValue(GlobalConstant.RealIpHeaderKey, out StringValues realIpAddress);
if (realIpExists) requestIp = realIpAddress.ToString();
```
**Neden:** İstemci kendi IP'sini seçebilir → `UserSession.IpAddress`, activity log ve olası IP-bazlı kısıtlar sahte. `UseForwardedHeaders` + `KnownProxies` yapılandırılmamış; `UseHttpsRedirection`/`UseHsts` de yok (reverse proxy'ye bırakıldığı varsayılmış ama belgelenmemiş).
**Düzeltme:** `ForwardedHeadersOptions` ile `KnownProxies/KnownNetworks`; `Connection.RemoteIpAddress` kullanın.

#### M7. Migration'lar her instance'ta startup'ta koşuyor; hatalar yutuluyor; `Program.cs` catch bloğu exit code 0 ile bitiyor
**Dosya:** `src/Milvaion.Api/Migrations/MigrationHostedService.cs:40-66`, `Program.cs:184-188`
**Neden:** N replika aynı anda `MigrateAsync` çalıştırırsa Postgres'te DDL çakışması; hata `Log.Error` ile yutuluyor ve uygulama yarım migrate edilmiş DB ile hizmet veriyor. `Program.cs`'deki `catch` startup hatasını konsola yazıp normal çıkıyor → orkestratör "başarılı" görür.
**Düzeltme:** Migration'ı ayrı bir init job/komut ile çalıştırın veya distributed lock; startup hatasında `Environment.ExitCode = 1` / rethrow.

#### M8. Gizli/duyarlı yapılandırma `AdminController.GetConfiguration` ve MCP `get_configuration` ile dönüyor
**Dosya:** `Application/Features/Configuration/GetSystemConfiguration/GetSystemConfigurationQueryHandler.cs:49-51`
```csharp
Redis = new RedisConfigDto { ConnectionString = _milvaionConfig.Redis.ConnectionString, ... }
```
**Neden:** Redis connection string şifre içerebilir (`host:6379,password=...`) ve `SystemAdministration.List` izniyle (ve MCP üzerinden bir LLM'e) dönüyor. DB için yalnızca Host/Database çıkarılmış — tutarsız.
**Düzeltme:** Redis için de yalnızca host/port; secret içerebilecek alanları maskeleyin.

#### M9. SQL Server desteği yok — `Milvaion.Api.SqlServer` boş, migration'lar Postgres'e özgü
**Dosya:** `src/Milvaion.Api.SqlServer/` (boş dizin), `Migrations/*` (`jsonb` ×21, `uuid`, `timestamp with time zone`, `ALTER TABLE "Users" ...`), `ExceptionMiddleware` (`PostgresException`), `AdminController` (bloat/cache-hit-ratio Postgres istatistikleri)
**Neden:** Proje adı/iddiası SQL Server desteği vaat ediyor ancak hiçbir dosya yok; migration'lar `jsonb`, `ExecuteUpdate`, PG trigger'ları kullanıyor. Yanıltıcı.
**Düzeltme:** Ya provider soyutlaması (ayrı migration assembly'leri, `HasColumnType` provider'a göre) ya da iddiayı kaldırın.

#### M10. `WorkflowEnhance` migration'ı `WorkflowSteps` tablosunu veri taşımadan düşürüyor; boş migration dosyası
**Dosya:** `Migrations/20260324194104_WorkflowEnhance.cs:14-25`, `Migrations/20260324185812_WorkflowDefinitionJsonb.cs` (0 byte)
**Neden:** `DropTable("WorkflowSteps")` sonra `Definition jsonb NOT NULL` ekleniyor — mevcut workflow adımları kaybolur, `NOT NULL` default'suz ekleme dolu tabloda başarısız olur. 0 byte'lık migration dosyası (Designer'ı yok) derlemeye girmiyor, kafa karıştırıcı artık.
**Düzeltme:** Veri taşıma SQL'i ekleyin; boş dosyayı silin.

#### M11. Refresh token akışında eski access token zorunlu, ancak `login/refresh` `[AllowAnonymous]` — süresi dolmuş token için `OnAuthenticationFailed` bypass'ı path sonuna göre
**Dosya:** `ServiceCollectionExtensions.cs:100-104`, `AccountController.cs:31`
```csharp
|| AccountController.LoginEndpointPaths.Exists(e => context.Request.Path.Value.EndsWith(e))   // "login", "refresh"
```
**Neden:** `EndsWith("login")`/`EndsWith("refresh")` path eşleşmesi kırılgan: `/api/v1/jobs/refresh` gibi ileride eklenecek herhangi bir route'ta geçersiz token hatası sessizce yutulur. Şu an hiçbir korumalı route bu son-ekle bitmediği için etkisi yok; tasarım riski.
**Düzeltme:** Endpoint metadata (`IAllowAnonymous`) üzerinden karar verin.

#### M12. CORS `AllowAll` varsayılan; `AllowAnyOriginWithCredentials` alanı okunmuyor
**Dosya:** `appsettings.json:5-16`, `ServiceCollectionExtensions.cs:514-566`
**Neden:** SPA aynı origin'den servis ediliyor; `AllowAnyOrigin + AllowAnyHeader` gereksiz geniş (SignalR/MCP'ye tarayıcı üzerinden 3. taraf sitelerden erişim — H3 ile birleşince veri sızıntısı). Config'deki `AllowAnyOriginWithCredentials` kodda hiç kullanılmıyor (ölü ayar).
**Düzeltme:** Prod'da origin listesi; ölü ayarı kaldırın.

---

### 🔵 LOW

#### L1. `MilvaionConfig` hem `AddSingleton(config)` hem `AddOptions<MilvaionConfig>()` — iki kaynak, `ValidateOnStart` yok
`Program.cs:34-36`, `InfraServiceCollectionExtensions.cs:157`. Section yoksa `AddSingleton(null)` fırlatır; iki kayıt farklı nesneler döndürür.

#### L2. `DateTimeOffset.Now` ile lockout süresi
`UpdateUserCommandHandler.cs:60` — sistem `UseUtcForDateTime=true`; diğer yerler `UtcNow`.

#### L3. Kopyala-yapıştır isimlendirme
`DashboardController.GetRolesAsync`, `ActivityLogsController.GetRolesAsync`, `AccountController.AccountDetailsAsync` (iki farklı işlem aynı isim), `HealthCheckController` route'u `api/health` iken Dockerfile HEALTHCHECK `/api/health` ✔ ama `Milvaion.Api.http` `/healthcheck/` (yanlış).

#### L4. `PATCH` fiili liste sorguları için kullanılıyor
Tüm `*Controller.Get*Async(...Query)` action'ları `[HttpPatch]`. Semantik olarak yanlış, cache/proxy davranışını bozar; `POST .../search` tercih edilmeli.

#### L5. Dead code
`Utils/ApiAuthAttribute.cs` (kullanılmıyor), `IAccountManager.GenerateToken` (20 yıllık token), `Migrations/20260324185812_WorkflowDefinitionJsonb.cs` (0 byte), `Cors.AllowAnyOriginWithCredentials`, `GlobalSuppressions.cs` içindeki `ResetUIRelatedDataAsync` bastırması.

#### L6. `/config.js` — `basePath` JS string'ine kaçışsız yazılıyor
`Program.cs:164-165`. Yalnızca admin config'i olduğundan risk düşük; `JsonSerializer.Serialize(basePath)` kullanın.

#### L7. `MissingResxKeyFinder.FindAndPrintToConsole()` prod'da her startup'ta reflection taraması
`Program.cs:18`. Development'a koşullayın.

#### L8. `MetricReportsController` kendi permission grubuna sahip değil (`ScheduledJobManagement.*` kullanıyor)
İzin granülaritesi tutarsız; rapor silme (`cleanup`) `ScheduledJobManagement.Delete` ile mümkün.

#### L9. Keycloak realm dosyası: `sslRequired: none`, test kullanıcısı `test/test`, `bruteForceProtected` yok
`keycloak/realm-milvaion.json`. Dev amaçlı olsa da README'de "prod'da kullanmayın" notu olmalı.

#### L10. Parola politikası zayıf
`appsettings.json:229-238`: `RequiredLength: 6`, PBKDF2 `IterationCount: 10000` (OWASP 2023 önerisi ≥ 600k SHA-256). Seed şifreleri `"string"`.

#### L11. `HttpExtensions.GetTokenFromHeader` `Split(" ").LastOrDefault()`
Boşluk içeren/bozuk `Authorization` header'ında yanlış token parçası; `Bearer ` önekini açıkça kontrol edin.

#### L12. API key JWT'de `ClaimTypes.Expired` (WS-Federation URI) ile son kullanma taşınıyor
`Utils/KeyHelper.cs:197,236`. Standart `exp` zaten `Expires` ile yazılıyor; ikinci alan gereksiz ve yanıltıcı adlandırılmış.

---

## 3. Mimari İyileştirme Önerileri

1. **Ortam kararını standartlaştırın.** `MILVA_ENV`'i kaldırın; `IHostEnvironment.IsDevelopment()/IsProduction()` kullanın. Dockerfile zaten `ASPNETCORE_ENVIRONMENT=Production` set ediyor — tüm dev-only bileşenler (DeveloperController, Scalar, seed şifreleri, Include Error Detail) buna bağlansın. `DeveloperController`'ı `IApplicationFeatureProvider<ControllerFeature>` ile Development dışında hiç register etmeyin.

2. **Secret'ları koddan çıkarın ve startup'ta doğrulayın.** `appsettings.json`'da secret alanları boş bırakılsın; `AddOptions<T>().ValidateDataAnnotations().ValidateOnStart()` ile "boş veya bilinen varsayılan" değerde başlatma reddedilsin. `SymmetricPublicKey`, `ApiKey.Secret`, DB/Redis/RabbitMQ/LDAP şifreleri için env var / secret store. JWT'de `ValidateIssuer/ValidateAudience=true` yapın.

3. **Yetki modelinde "korunan çekirdek" tanımlayın.** `App.SuperAdmin` permission'ı, `SuperAdmin` rolü (Id=1) ve `rootuser` (Id=1) için domain seviyesinde değişmezlik kuralları: atanamaz, silinemez, yeniden adlandırılamaz; kullanıcı kendi rollerini/iznini yükseltemez. API key'lere SuperAdmin verilemez. Bu kuralları tek bir `AuthorizationInvariants` servisinde toplayıp Role/User/ApiKey handler'larında ve MCP'de kullanın.

4. **Token yaşam döngüsünü sunucu tarafında bağlayın.** Access token'a `sid` claim'i; `JwtBearerEvents.OnTokenValidated`'da Redis destekli session doğrulaması; logout/rol değişimi/parola değişimi/kullanıcı silme → anında geçersizleştirme. Refresh token'ları hash'li saklayın, rotasyon + reuse detection uygulayın.

5. **Kimlik şemalarını endpoint sınıfına göre ayırın.** Üç policy: `Interactive` (yalnızca JWT/OIDC — hesap ve API key yönetimi), `Machine` (yalnızca API key — MCP, entegrasyonlar), `Any` (her ikisi — okuma/operasyon). `ApiAuthAttribute`'u ya kullanın ya silin.

6. **SignalR'ı yetkilendirin ve ölçeklendirin.** Hub'a `[Authorize]`, grup aboneliğinde permission kontrolü, `Clients.All` yerine `Clients.Group("jobs-readers")`, çoklu instance için Redis backplane; publisher'da `Task.Run` yerine `Parallel.ForEachAsync` + gerçek CancellationToken.

7. **Gözlemlenebilirlik ve hata sözleşmesi.** Serilog'daki bozuk filtreyi kaldırın; `ExceptionMiddleware`'i `IProblemDetailsService`/RFC 9457'ye taşıyın; 5xx için 500 dönün; log'larda `Authorization`/`X-ApiKey`/`Cookie` header'larını redakte edin. `/api/metrics` ve health detaylarını ayrı yönetim portuna alın.

8. **Ağ kenarı sertleştirme.** `UseForwardedHeaders` (KnownProxies), `UseHsts`/`UseHttpsRedirection` (proxy TLS terminate ediyorsa belgeleyin), `AddRateLimiter` (login/refresh/MCP), CORS origin listesi, request body limit'lerini açıkça belirleyin.

9. **Migration stratejisi.** Startup'ta `MigrateAsync` yerine `dotnet ef database update` / init container; migration'larda veri kaybına yol açan `DropTable`'lar için taşıma SQL'i; SQL Server desteği gerçekten hedefleniyorsa provider-bağımsız model + iki migration assembly'si; değilse `Milvaion.Api.SqlServer` projesini ve dokümantasyondaki iddiayı kaldırın.

10. **Küçük temizlikler.** Ölü kodları silin (`ApiAuthAttribute`, `GenerateToken`, boş migration, `AllowAnyOriginWithCredentials`), controller metot adlarını düzeltin, `PATCH`-ile-listeleme konvansiyonunu `POST /search`'e çevirin, `DateTimeOffset.Now` → `UtcNow`, parola politikasını (uzunluk ≥ 12, PBKDF2 iterasyon ≥ 600k veya Argon2id) güncelleyin.

---

# BÖLÜM D — SDK & Workers


**Kapsam:** `src/Sdk/*` (Milvasoft.Milvaion.Sdk, Sdk.Worker, Sdk.Worker.Hangfire, Sdk.Worker.Quartz, Sdk.Worker.Testing) ve `src/Workers/*` (EmailWorker, HttpWorker, SqlWorker, ReporterWorker, MilvaionMaintenanceWorker, Sample*, Milvasoft.Templates.Milvaion). Karşılaştırma için API tarafındaki tüketiciler (`StatusTrackerService`, `LogCollectorService`, `JobDispatcherService`/`RabbitMQPublisher`, `FailedOccurrenceHandler`, `JobCancellationService`) ve `docs/` altındaki WORKER-SDK / 04 / 05 / 08 / 14 / 15 dokümanları okundu.

Satır numaraları inceleme anındaki dosyalara aittir. Güven seviyesi belirtilmeyen bulgular kodda doğrulanmıştır; teorik/çevresel olanlar "Güven: orta/düşük" ile işaretlendi.

---

## 1. Genel Bakış ve Kalite Değerlendirmesi

**Mimari özet:** Worker SDK, her `JobConsumers` girdisi için ayrı bir `JobConsumer` (BackgroundService + kendi RabbitMQ bağlantısı) açar; mesaj gövdesi API'nin EF entity'si olan `ScheduledJob`'ın camelCase JSON'udur. Job çalıştırma `JobExecutor` üzerinden reflection ile yapılır; durum/log yayını `OutboxService` → `StatusUpdatePublisher`/`LogPublisher` (her biri kendi bağlantısıyla) üzerinden, kesinti durumunda SQLite tabanlı `LocalStateStore`'a düşer ve `SyncOrchestratorService` bunları sonradan gönderir. Heartbeat/registration `WorkerListenerPublisher`, iptal ise Redis Pub/Sub (`CancellationListenerService`) ile yapılır.

**Olumlu yönler**
- Mesaj sözleşmeleri (queue adları, `WorkerConstant`, `JobStatusUpdateMessage`, `WorkerLogBatchMessage`, `WorkerDiscoveryRequest`, `DlqJobMessage`, iptal mesajı) SDK ile API arasında paylaşılan tiplerden geliyor ve her iki tarafta `PropertyNameCaseInsensitive` kullanılıyor; **alan adı/enum düzeyinde bir uyumsuzluk bulunmadı.**
- Ack, job tamamlandıktan sonra gönderiliyor (at-least-once), `JobConsumer` içinde kanal erişimi `_channelLock` ile serileştirilmiş, ACK/NACK asla cancellation token ile iptal edilmiyor — doğru tasarım.
- `PermanentJobException`, DLQ, üstel geri çekilme, redelivery tespiti, job başına heartbeat, `DynamicEnum` ile şema üretimi gibi olgunlaşmış özellikler var.
- `JobTestRunner` gerçekten bağımlılıksız çalışıyor ve template projeleri güncel SDK API'siyle uyumlu.

**Zayıf yönler (özet)**
- Offline resilience katmanının en kritik yolu (RabbitMQ kesikken final status) sorgu filtresi yüzünden **hiç sync edilmiyor** (Critical).
- `OfflineResilience.Enabled=false` yapıldığında worker **hiçbir status/log yayınlamıyor** (High).
- Publisher'larda kanal düzeyi hatadan sonra kalıcı bozulma; kanal paylaşımı kilitsiz.
- `IJobWithResult<T>` (sync) job'larda reflection `Invoke` istisnaları sararak `PermanentJobException`/iptal sınıflandırmasını bozuyor.
- Graceful shutdown sırası (kanal kapatılmadan önce job'lar iptal edilmiyor) dokümandaki akışla çelişiyor; aynı worker yeniden başlarsa job "Cancelled" olarak kalıp bir daha çalışmıyor.
- HttpWorker/SqlWorker "tasarım gereği" güçlü, ancak SSRF/rastgele SQL için hiçbir korkuluk yok; committed appsettings'lerde gerçek DB parolası var.
- Bakım worker'ındaki `OccurrenceArchiveJob` var olmayan bir kolona (`ScheduledTime`) ve yanlış tipte bir kolona (`Level integer`) yazmaya çalıştığı için çalışamaz.
- Çok sayıda `Console.WriteLine`, kopyala-yapıştır (iki `ExternalJobPublisher`, 10 neredeyse aynı Reporter job'u), ölü seçenekler ve ölü kod.

---

## 2. Bulgular

### 2.1 Critical

#### C1. Offline outbox: job finalize edildikten sonra bekleyen status/log kayıtları asla sync edilmiyor
- **Dosya:** `src/Sdk/Milvasoft.Milvaion.Sdk.Worker/Persistence/LocalStateStore.cs:783-793` (`GetNotSyncedStatusUpdates`), `:811-821` (`GetPendingLogs`); tetikleyen akış `src/Sdk/Milvasoft.Milvaion.Sdk.Worker/RabbitMQ/JobConsumer.cs:1065-1077`.
- **Alıntı:**
  ```sql
  FROM StatusUpdates s
  LEFT JOIN JobExecutions j ON s.OccurrenceId = j.OccurrenceId
  WHERE s.IsSynced = 0
  AND (j.IsFinalized IS NULL OR j.IsFinalized = 0)
  ```
  ```csharp
  await _outboxService.PublishStatusUpdateAsync(... result.Status ...);   // RabbitMQ yoksa → StoreStatusUpdateAsync (IsSynced=0)
  await _outboxService.GetLocalStore().FinalizeJobAsync(correlationId, result.Status, ...); // IsFinalized = 1
  ```
- **Neden sorun:** Outbox'ın var olma amacı tam olarak bu senaryo: job bitti, RabbitMQ yok, final `Completed/Failed` lokal DB'ye yazıldı. Hemen ardından `FinalizeJobAsync` `IsFinalized=1` yapıyor; sync sorguları finalize edilmiş job'ların kayıtlarını **dışlıyor**. Sonuç: final status hiç gönderilmiyor, `CleanupSyncedRecordsAsync` retention süresi sonunda kaydı siliyor. API tarafında occurrence `Running`'de kalır, zombie dedektörü onu `Failed`'a çeker; aslında başarılı olan job "başarısız" görünür ve retry/alert mekanizmaları tetiklenir. Aynı durum job sonunda yazılan loglar için de geçerli.
- **Düzeltme:** Sorgudaki `IsFinalized` filtresini kaldırın (finalize, redelivery idempotency'si için gerekli, sync filtresi için değil). `FinalizeJobAsync` içindeki "IsSynced=1 olanları sil" güvenlik ağı yeterli. Ek olarak `SyncStatusUpdatesAsync`'te `RetryCount >= maxRetries` olan kayıtları "synced" işaretleyip **sessizce düşürmek** yerine Warning/metric ile görünür kılın.

### 2.2 High

#### H1. `OfflineResilience.Enabled=false` → worker hiçbir status/log yayınlamıyor
- **Dosya:** `WorkerServiceCollectionExtensions.cs:251-275` (OutboxService sadece Enabled iken kayıtlı), `JobConsumer.cs:649, 688, 1046` (`if (_outboxService != null)`), `JobContext.cs:181-184`.
- **Neden sorun:** Tüm `PublishStatusUpdateAsync`/`PublishLogAsync` çağrıları `_outboxService` üzerinden. Seçenek kapatılınca `Running/Completed/Failed` hiç gönderilmez, loglar "OutboxService is NULL! Logs will not be persisted." uyarısıyla çöpe gider, idempotency kontrolü de devre dışı kalır. Bir "resilience" bayrağının temel raporlamayı kapatması beklenmez; docs bunu söylemiyor.
- **Düzeltme:** `OutboxService`'i her zaman kaydedin; `Enabled=false` iken `ILocalStateStore` yerine no-op bir store kullanın (ya da OutboxService'i "publish-only" moduna alın).

#### H2. `StatusUpdatePublisher` / `LogPublisher`: kanal kapanınca kalıcı olarak bozuluyor, yeniden bağlanma yok, kanal paylaşımı kilitsiz
- **Dosya:** `StatusUpdatePublisher.cs:130-155`, `LogPublisher.cs:180-205`.
- **Alıntı:**
  ```csharp
  if (_connection == null || !_connection.IsOpen) { ...CreateConnectionAsync... _channel = ... QueueDeclareAsync(...) }
  ```
- **Neden sorun:**
  1. Sadece bağlantı kontrol ediliyor; kanal broker tarafından kapatılırsa (ör. `QueueDeclare` 406 PRECONDITION_FAILED — API ile `QueueType` farklıysa tam da bu olur, ya da herhangi bir kanal-hatası) `_connection.IsOpen` true kalır, `_channel` ölü kalır ve tüm yayınlar süresiz `AlreadyClosedException` fırlatır. `DisposeConnectionAsync` yalnızca `DisposeAsync`'te çağrılıyor ("Dispose failed connection to allow reconnection" yorumu yanıltıcı). Outbox'a düşen kayıtlar aynı publisher'la sync edilmeye çalışıldığı için restart'a kadar hiçbir status ulaşmaz.
  2. `AutomaticRecoveryEnabled` bu factory'lerde set edilmemiş.
  3. `EnsureConnectionAsync` eşzamanlı çağrılarda yarışıyor (iki bağlantı açılıp biri sızabilir). Aynı `IChannel` üzerinde N paralel job + N heartbeat task'ı kilitsiz `BasicPublishAsync` yapıyor. RabbitMQ.Client dokümanı `IChannel`'ın thread'ler arasında paylaşılmamasını söylüyor; `JobConsumer` kendi kanalını kilitliyorken bu iki sınıf kilitlemiyor (tutarsız). Güven: Kanal-kapalı-kalır kısmı kesin; eşzamanlı publish'in v7'de veri bozması "orta" (v7 frame yazımını atomik yapıyor ama confirm takibi/`_confirmSemaphore` davranışı garanti verilmiyor).
  4. Mesajlar `Persistent=false` (BasicProperties verilmiyor): kuyruk durable ama broker restart'ında status/log mesajları kaybolur — PublisherConfirms ile alınan "data safety" sözü yarım.
- **Düzeltme:** `EnsureConnectionAsync`'te `_channel?.IsOpen` de kontrol edin, hata durumunda `DisposeConnectionAsync` çağırıp yeniden kurun (Quartz/Hangfire `ExternalJobPublisher` zaten böyle yapıyor — onu ortak sınıfa taşıyın), `SemaphoreSlim` ile serileştirin, `AutomaticRecoveryEnabled=true`, `new BasicProperties { Persistent = true }` gönderin.

#### H3. `WorkerListenerPublisher.SendAllHeartbeatsAsync` eşzamanlı ve kanal hatasından sonra iyileşmiyor
- **Dosya:** `WorkerListenerPublisher.cs:117-129, 213-247`; çağıran `JobConsumer.cs:929-932`.
- **Neden sorun:** Her job başlangıcı/bitişi `SendImmediateHeartbeatAsync` ile aynı `_channel` üzerinde yayın yapıyor (debounce 1 sn ama N job aynı milisaniyede bitebilir); periyodik döngü de aynı kanalı kullanıyor, kilit yok. Kanal kapanırsa iç döngüdeki `catch` sadece Warning loglar; dış `while`+retry sadece istisna dışarı çıkınca çalışır → worker "canlı" olduğu halde heartbeat göndermeyi bırakır ve API onu offline sayar. Ayrıca `Version = Assembly.GetExecutingAssembly()` SDK'nın sürümünü raporlar, worker uygulamasınınkini değil.
- **Düzeltme:** Yayınları kilitleyin, `SendAllHeartbeatsAsync` içinde `_channel.IsOpen` kontrolü + yeniden kanal açma; `Assembly.GetEntryAssembly()`.

#### H4. `JobExecutor.InvokeJobAsync`: sync `IJobWithResult<T>` için `MethodInfo.Invoke` istisnaları `TargetInvocationException` içine sarıyor
- **Dosya:** `src/Sdk/Milvasoft.Milvaion.Sdk.Worker/Core/JobExecutor.cs:298-313`.
- **Alıntı:**
  ```csharp
  var executeMethod = jobWithResultInterface.GetMethod("Execute");
  var result = executeMethod!.Invoke(jobInstance, [context]);
  ```
- **Neden sorun:** `Invoke` (DoNotWrapExceptions olmadan) her istisnayı `TargetInvocationException` içine sarar. `catch (PermanentJobException)` ve `catch (OperationCanceledException)` yakalayamaz → kalıcı hatalar retry edilir, iptal/timeout `Failed` olarak raporlanır, `Exception` alanı `System.Reflection.TargetInvocationException` ile başlar. Ayrıca bu sync yol `Task.Run` ile sarılmadığı için (IJob'da sarılıyor) consumer dispatch thread'ini bloklar — tutarsız. `IAsyncJobWithResult<>` için de `task.GetType().GetProperty("Result")` reflection'ı gereksiz.
- **Düzeltme:** Reflection yerine `dynamic`/generic yardımcı (`InvokeGeneric<TResult>`) ya da `BindingFlags.DoNotWrapExceptions`; sync yolu `Task.Run` ile aynı şekilde sarın. `IAsyncJobWithResult<>` için `await (dynamic)task` veya cached delegate kullanın.

#### H5. Graceful shutdown sırası: kanal kapatıldıktan sonra job'lar iptal ediliyor → ACK atılamıyor, aynı worker'da job kalıcı "Cancelled" kalıyor
- **Dosya:** `JobConsumer.cs:235-269` (`StopAsync`), `:216-226` (`ExecuteAsync` catch), `LocalStateStore.cs:437-468, 849-855`.
- **Neden sorun:** `StopAsync` önce shutdown heartbeat gönderip `_channel.CloseAsync()` ve `_connection.CloseAsync()` yapıyor, **sonra** `base.StopAsync` ile `stoppingToken` iptal ediliyor. Yani çalışan job'lar kanal kapandıktan sonra iptal ediliyor; `JobExecutor` `Cancelled` sonucu döndürüyor, `PublishFinalStatusAndFinalizeAsync` bunu API'ye yayınlayıp lokal store'da `IsFinalized=1 / Cancelled` yazıyor; `SafeAckAsync` kanal kapalı olduğu için sessizce dönüyor → mesaj broker'da unacked kalıp yeniden teslim ediliyor. Redelivery **aynı worker'a** (aynı `./worker_data` volume ile restart) gelirse idempotency kontrolü "finalized" görüp mesajı ACK'leyip atlıyor: job hiç tamamlanmadan `Cancelled` olarak kalır. Farklı bir worker'a giderse yeniden çalışır ama API `Cancelled→Running` geçişini reddedip sonra `Completed` yazar (kafa karıştırıcı geçmiş). Docs (08-reliability "Stop accepting new messages → cancel jobs → wait → ACK") tam tersini vaat ediyor. Ayrıca bekleme döngüsü `MaxParallelJobs × 30 s`'ye kadar sürebilir.
- **Düzeltme:** `StopAsync`'te önce `BasicCancelAsync(consumerTag)` (yeni teslimatı durdur), sonra `base.StopAsync` (job'ları iptal et ve bitmelerini bekle), en son kanalı kapatın. `Cancelled` (sistem kaynaklı) sonuçları lokal store'da finalize **etmeyin** (sadece kullanıcı iptali için finalize edin) ya da `Cancelled` status'ünü ACK ile birlikte atomik gönderin. Bekleme için tek bir toplam süre (HostOptions.ShutdownTimeout'a bağlı) kullanın.

#### H6. Retry gecikmesi consumer slotunu tutuyor; işlem sırasında worker ölürse retry kaybolmuyor ama occurrence `Running`'de kalıyor
- **Dosya:** `JobConsumer.cs:954-998` (`RetryJobAsync`), `:779-785`.
- **Alıntı:**
  ```csharp
  await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);  // 5,10,20 s ... semaphore + prefetch slotu tutarak
  await SafePublishAsync(exchange: "", routingKey: queueName, ...);
  await SafeAckAsync(deliveryTag, logger, job.Id);
  ```
- **Neden sorun:** Backoff, mesaj unacked ve `_concurrencySemaphore` tutulurken `Task.Delay` ile yapılıyor. `MaxParallelJobs=1` olan bir consumer'da tek başarısız job 5+10+20 s boyunca kuyruğu bloklar; yüksek hata oranında tüm slotlar "uyuyan" job'larla dolar. Retry yolunda API'ye ara bir `Failed`/`Retrying` status yayınlanmadığı için dashboard'da occurrence dakikalarca `Running` görünür; job heartbeat'i de durduğu için zombie dedektörü onu `Failed`'a çekebilir. Ayrıca `MaxRetries` ve `BaseRetryDelaySeconds` bilgisi `OccurrenceLog` üzerinden geçiyor ama API tarafındaki `x-retry-count` başlığı sadece DLQ'da okunuyor.
- **Düzeltme:** Gecikmeli yeniden yayın için RabbitMQ delayed-message exchange ya da TTL+DLX "retry" kuyruğu kullanın; mesajı hemen yeniden yayınlayıp ACK'leyin. Retry yolunda ara bir status (ör. `Queued` + `Exception`) yayınlayın.

#### H7. HttpWorker: sınırsız SSRF yüzeyi, TLS doğrulamasını kapatma ve makinedeki sertifikaları kullanma job datasından kontrol ediliyor
- **Dosya:** `src/Workers/HttpWorker/Jobs/RequestSenderJob.cs:42-47, 154-238, 362-406, 488-492`.
- **Neden sorun:** URL, proxy, redirect takibi, `IgnoreSslErrors`, `ClientCertificate.Thumbprint` (worker makinesinin `LocalMachine\My` deposu), OAuth2 `TokenUrl`, multipart `ContentUrl` — hepsi job datasından geliyor; hiçbir allow/deny listesi, özel IP aralığı (10/8, 169.254.169.254 metadata, localhost) veya şema kısıtı yok. Dashboard'da job oluşturabilen herkes worker'ın ağından iç servislere/istek yapar, cloud metadata okur, worker'ın istemci sertifikalarıyla mTLS yapar ve yanıt gövdesini `Result` olarak DB'ye alır. `ContentUrl` ve OAuth için `new HttpClient()` (timeout 100 s, redirect açık). 14-http-worker.md bunların hiçbirinden bahsetmiyor. Tasarım gereği "generic" olması bunu değiştirmez; en azından opt-in korkuluk gerekir.
- **Düzeltme:** `HttpWorkerOptions` ile `AllowedHosts/BlockedCidrs` (varsayılan: private/link-local/loopback engelli), `AllowIgnoreSslErrors=false`, `AllowThumbprintCertificates=false`, redirect hedeflerini de doğrulayan bir `DelegatingHandler`, `ContentUrl` için aynı kurallar + boyut sınırı. Dokümana "Security" bölümü ekleyin.

#### H8. SqlWorker: rastgele SQL, varsayılan olarak Milvaion'ın kendi veritabanında; committed parola
- **Dosya:** `src/Workers/SqlWorker/appsettings.json:16`, `appsettings.Development.json:12`, `ReporterWorker/appsettings.json:14`, `MilvaionMaintenanceWorker/appsettings*.json:14/10`; `SqlExecutionJob.cs` (korkuluk yok).
- **Alıntı:** `"ConnectionString": "Host=postgres;...;Username=postgres;Password=N4SQp.qW>6?xwWzg;..."`
- **Neden sorun:** (1) Aynı gerçek görünümlü parola dört projede repo'ya gömülü ve `postgres` süper kullanıcısı. (2) SqlWorker'ın tek örnek bağlantısı `MilvaionDb`; dashboard üzerinden job oluşturabilen herkes `SqlExecutionJob` ile Milvaion'ın kendi tablolarını (kullanıcılar, API key'ler, job'lar) okuyup değiştirebilir — yetki yükseltme. (3) `SqlJobData.Query` için read-only modu, statement allow-list'i, `MaxRows` varsayılanı (0 = sınırsız) yok; `QueryAsync` tüm satırları belleğe alıp sonra `.Take(maxRows)` uyguluyor (`SqlExecutionJob.cs:203-209`), sonuç JSON'u `Result` kolonuna sınırsız yazılıyor. (4) `IsPermanentError` mesaj metnine göre karar veriyor (yerelleştirilmiş DB mesajlarında kırılır). 15-sql-worker.md secrets'ı söylüyor ama "en az yetkili DB kullanıcısı / Milvaion DB'yi bağlamayın" demiyor.
- **Düzeltme:** Parolaları kaldırıp env/secret referansına çevirin, örnek bağlantıyı Milvaion DB'den ayırın; `SqlConnectionConfig`'e `ReadOnly`, `AllowedCommandTypes`, `MaxRowsHardLimit`, `MaxResultBytes` ekleyin; `MaxRows>0` iken `LIMIT`/`TOP` ya da `QueryUnbufferedAsync` kullanın; dokümana least-privilege uyarısı ekleyin.

#### H9. `OccurrenceArchiveJob` çalışamaz: var olmayan kolon ve yanlış tip
- **Dosya:** `src/Workers/MilvaionMaintenanceWorker/Jobs/OccurrenceArchiveJob.cs:167-190, 215-228, 254-262, 275-280, 292-303`.
- **Alıntı:**
  ```sql
  "Level" integer NOT NULL,          -- JobOccurrenceLog.Level string
  ...
  SELECT jo."Id", ..., jo."ScheduledTime", ...   -- JobOccurrence'ta ScheduledTime yok
  ```
- **Neden sorun:** `JobOccurrence`'ta `ScheduledTime` özelliği/kolonu yok (repo genelinde tek referans bu dosya) → INSERT `column jo."ScheduledTime" does not exist` ile patlar. Düzeltilse bile `Level` text→integer dönüşümü hata verir; `Category`, `ExceptionType`, `WorkerId`, `ExecutionTimeoutSeconds`, `Workflow*` kolonları arşive taşınmıyor (veri kaybı). Arşivleme CTE'si ile occurrence DELETE'i ayrı iki statement ve transaction yok; ikinci `DELETE ... LIMIT` `ORDER BY` içermediği için arşivlenen kümeyle silinen küme farklı olabilir → arşivlenmeden silinen occurrence (kalıcı veri kaybı) veya arşivlenip silinmeyen occurrence (sonraki batch'te PK ihlali). Aynı "iki ayrı LIMIT sorgusu" deseni `OccurrenceRetentionJob.cs:129-172`'de de var (orada sonuç nihai olarak tutarlı ama loglar occurrence'tan önce silinebiliyor).
- **Düzeltme:** Şemayı EF modelinden türetin (ya da `CREATE TABLE ... (LIKE "JobOccurrences" INCLUDING ALL)`), tüm işlemi tek transaction'da ve aynı `to_archive` CTE'si üzerinden DELETE yapın, entegrasyon testi ekleyin.

### 2.3 Medium

#### M1. `JobContext.Log` — `ExecutorJobConsumerConfig` null ise NRE; `JobNameInWorker` eşleşmesi büyük/küçük harf tutarsız
- **Dosya:** `JobContext.cs:90, 170`; `JobConsumer.cs:716, 944-949`; `JobConsumerOptions.cs:9`.
- **Neden sorun:** `ResolveJob` `OrdinalIgnoreCase` ile job bulurken `_jobConfigs.GetValueOrDefault(job.JobNameInWorker)` varsayılan (case-sensitive) `Dictionary`. `JobNameInWorker` küçük harfle gelirse job bulunur ama config null olur; ilk `context.Log(...)` çağrısı `ExecutorJobConsumerConfig.LogUserFriendlyLogsViaLogger` üzerinde NRE fırlatır → job "Failed" + 3 retry + DLQ, gerçek sebep kullanıcıya görünmez. `JobTestRunner` config verdiği için testlerde yakalanmaz.
- **Düzeltme:** `JobConsumerOptions`'ı `StringComparer.OrdinalIgnoreCase` ile kurun (`IOptions` bind'ı desteklemiyorsa lookup'ı normalize edin) ve `JobContext`'te null-safe erişim.

#### M2. `LogPublisher` hataları yutuyor → outbox'a hiç düşmüyor; bellek tamponu sınırsız, sıra bozuluyor
- **Dosya:** `LogPublisher.cs:53-107, 161-171`; `OutboxService.cs:108-127`.
- **Neden sorun:** `PublishLogAsync` her zaman başarıyla döner (sadece enqueue). `OutboxService`'in "yayın başarısızsa lokal store'a yaz" dalı loglar için yalnızca `IsRabbitMQHealthy=false` iken çalışır; monitor 30 s'de bir baktığı için kesintinin ilk 30-60 s'sinde flush'lar hata verir, loglar kuyruğun **sonuna** yeniden eklenir (sıra bozulur) ve yalnızca bellekte tutulur (crash'te kayıp, sınırsız büyüme). `_flushTimer ??=` yarış koşulunda birden fazla timer oluşabilir; timer callback'i `async void`.
- **Düzeltme:** Flush başarısızlığında batch'i `ILocalStateStore.StoreLogAsync`'e yazın (ya da `PublishLogAsync` flush hatasını yüzeye çıkarsın), tampon için üst sınır + `Channel<T>` kullanın, timer'ı yapıcıda oluşturun.

#### M3. Redis Pub/Sub tabanlı iptal kaybolabilir
- **Dosya:** `CancellationListenerService.cs:34-67`; API `JobCancellationService.cs`.
- **Neden sorun:** Pub/Sub fire-and-forget: worker o an Redis'e bağlı değilse, ya da iptal mesajı job worker tarafından henüz tüketilmeden (Queued) yayınlandıysa iptal kaybolur; job normal çalışır. `Guid.Parse(cancellationRequest.CorrelationId)` null'da fırlatır (yakalanıyor). Sözleşme (PascalCase `CorrelationId/JobId/OccurrenceId/Reason`) API ile uyumlu.
- **Düzeltme:** İptal isteğini Redis'te `cancel:{occurrenceId}` anahtarı (TTL) olarak da yazın; `JobConsumer` job'u başlatmadan önce bu anahtarı kontrol etsin; job heartbeat'i sırasında periyodik kontrol.

#### M4. `JobDataTypeHelper.GetJobDataType` sonuç-tipli job'larda `TResult`'ı JobData şeması sanıyor; özyineleme koruması yok
- **Dosya:** `Utils/JobDataTypeHelper.cs:17-23, 63-89, 244-249`.
- **Neden sorun:** `_genericJobInterfaces` 1-argümanlı `IJobWithResult<>`/`IAsyncJobWithResult<>`'ı içeriyor; `IAsyncJobWithResult<string>` olan tüm Maintenance job'ları için `string` "JobData tipi" sayılıp `{"title":"String","properties":{}}` şeması dashboard'a gönderiliyor. `IAsyncJobWithResult<TData,TResult>` için `GetInterfaces()` sırası garanti olmadığından TResult dönebilir. `GenerateProperties` kendine referans veren tipte (`Parent` gibi) sonsuz özyineleme → `StackOverflow` ile worker açılışta çöker.
- **Düzeltme:** 1-arg result arayüzlerini `_genericJobInterfaces`'ten çıkarın; ziyaret edilen tip kümesi/derinlik sınırı ekleyin.

#### M5. Zaman aşımı "yumuşak": token'a saygı göstermeyen job süresiz çalışmaya devam eder, slot işgal eder
- **Dosya:** `JobExecutor.cs:66-75, 104-125, 315-318`.
- **Neden sorun:** `TimedOut` sonucu yalnızca job `OperationCanceledException` fırlatırsa üretilir. `Task.Run(() => syncJob.Execute(context), token)` token'ı sadece başlamadan önce kontrol eder. CPU-bound/sync veya token'ı geçirmeyen job'lar timeout'a rağmen çalışmaya, `_concurrencySemaphore` slotunu ve unacked mesajı tutmaya devam eder; API'ye hiçbir zaman final status gitmez (zombie dedektörüne kalır). Docs "it will be cancelled and marked as TimedOut" diyor.
- **Düzeltme:** `Task.WhenAny(jobTask, Task.Delay(timeout))` ile sert zaman aşımı: `TimedOut` yayınla, ACK/DLQ kararını ver, kaçak task'ı loglayıp izle (`Task.ContinueWith`). Dokümana "cooperative cancellation" notu ekleyin.

#### M6. `ResolveJob` her mesajda tüm `IJobBase` implementasyonlarını (ve bağımlılıklarını) instantiate ediyor; aynı isimli sınıflar sessizce çakışıyor
- **Dosya:** `JobConsumer.cs:944-949`; `WorkerServiceCollectionExtensions.cs:43-46, 108-118`.
- **Neden sorun:** `GetServices<IJobBase>()` N job için N constructor + scoped bağımlılık (DbContext, HttpClient...) çalıştırır; yalnızca biri kullanılır. `ToDictionary(t => t.Name)` aynı kısa ada sahip iki job'da `ArgumentException` ile açılışta patlar (mesaj anlaşılmaz). `Assembly.GetEntryAssembly().GetTypes()` `ReflectionTypeLoadException` fırlatabilir ve başka assembly'deki job'lar bulunmaz (docs'ta belirtilmemiş).
- **Düzeltme:** Keyed DI (`AddKeyedTransient<IJobBase>(jobName, type)`) ile `GetRequiredKeyedService`; `Type.FullName` tabanlı çakışma tespiti; `AddMilvaionWorkerWithJobs(params Assembly[])` overload'u.

#### M7. Hangfire adaptörü: `DateTime.TryParse` ile UTC "O" string'i yerel saate çevriliyor → `DurationMs` saat dilimi kadar hatalı; `OnCreated` try/catch dışında
- **Dosya:** `Hangfire/Filters/MilvaionJobFilter.cs:68-98, 174-176`.
- **Alıntı:** `var startTime = DateTime.TryParse(startTimeStr, out var st) ? st : endTime; var durationMs = (long)(endTime - startTime).TotalMilliseconds;`
- **Neden sorun:** `"...Z"` parse edilince `Kind=Local` ve yerel saate çevrilmiş değer döner; `DateTime.UtcNow - local` çıkarma `Kind`'ı yok sayar → UTC+3 makinede süre 3 saat eksik/negatif. Sınıf yorumu "All methods are wrapped in try-catch" derken `OnCreated` sarılı değil ve `context.Job` null'da NRE ile Hangfire'ın job oluşturmasını bozar. `OnStateElection` çalışmamış (correlation'ı olmayan) job'ların silinmesinde rastgele `CorrelationId` ile hayali `Cancelled` occurrence üretir. Tüm yayınlar `Task.Run` fire-and-forget → Starting/Completed sırası garanti değil.
- **Düzeltme:** `DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)`; `OnCreated`'ı sarın; correlation yoksa Cancelled yayınlamayın; publish'i `PerformContext` içinde senkron `await` edin (filter async değilse `GetAwaiter().GetResult()` yerine bounded channel'a yazın).

#### M8. Quartz/Hangfire `ExternalJobPublisher` birebir kopya; hatalar yutulup lokal store'a düşmüyor
- **Dosya:** `Hangfire/Services/ExternalJobPublisher.cs` ↔ `Quartz/Services/ExternalJobPublisher.cs` (diff: 3 satır).
- **Neden sorun:** 160 satırlık sınıf iki pakette kopya; `IExternalJobPublisher` arayüzü iki farklı namespace'te tanımlı (aynı uygulamada ikisi de referans alınırsa çakışır). Kesintide occurrence event'leri sessizce kaybolur (`catch → Error log`). Hangfire `HangfireWorkerStartupService` recurring job yoksa host `StartAsync`'i 10×2 s bloklar.
- **Düzeltme:** Sınıfı `Sdk.Worker`'a taşıyın, `OutboxService` üzerinden yayınlayın.

#### M9. Wire sözleşmesi olarak EF entity (`ScheduledJob`) kullanılıyor; SDK job'lara audit alanları ve `AutoDisableSettings` gibi iç alanları açıyor
- **Dosya:** `Sdk/Domain/ScheduledJob.cs`, `IJobContext.Job`, API `RabbitMQPublisher.cs:46`.
- **Neden sorun:** Entity'ye eklenen/çıkarılan her alan sessiz bir wire değişikliği; `JobVersions`, `AutoDisableSettings`, `CreatorUserName`, `Occurrences` gibi alanlar job koduna sızıyor; mesajda versiyon/`schemaVersion` yok. Worker retry'ında mesaj `JsonSerializer.Serialize(job)` ile **varsayılan** (PascalCase, cycle koruması yok) ayarlarla yeniden serileştiriliyor — API camelCase + `IgnoreCycles` kullanıyor; case-insensitive okuma sayesinde bugün çalışıyor ama kırılgan.
- **Düzeltme:** `JobDispatchMessage` DTO'su (`Id, JobNameInWorker, DisplayName, JobData, ExecutionTimeoutSeconds, Version, SchemaVersion`) tanımlayıp her iki tarafta paylaşılan `JsonSerializerOptions` kullanın; `IJobContext.Job`'u bu DTO'ya çevirin (breaking change, major sürümde).

#### M10. `HttpWorker` yanıt boyutu ve HTTP-düzeyi + SDK-düzeyi retry çarpımı
- **Dosya:** `RequestSenderJob.cs:84, 528-584, 613-631, 660-669`.
- **Neden sorun:** `SendAsync` varsayılan `ResponseContentRead` ile tüm gövde belleğe alınır; `MaxResponseSizeBytes` sadece `Content-Length` varsa ve **indirildikten sonra** kontrol edilir; `ReadAsStringAsync` tamamını okur (`MaxResponseLength` yalnızca `Result`'ı kırpar). Son deneme 503 döndüğünde `EnsureSuccessStatusCode` normal exception fırlatır → SDK 3 kez daha retry eder (her biri kendi iç retry'ıyla) → 4×(1+MaxRetries) istek. Başarısız ara `HttpResponseMessage`'lar dispose edilmiyor. `PermanentJobException` mesajına tam yanıt gövdesi ekleniyor.
- **Düzeltme:** `HttpCompletionOption.ResponseHeadersRead` + sınırlı stream okuma; tükenen HTTP retry sonrası `PermanentJobException` ya da SDK retry'ını job datasından kapatma; gövdeyi kırpın.

#### M11. `SyncOrchestratorService`/`OutboxService`: `MaxSyncRetries` seçeneği okunmuyor, aşan kayıtlar sessizce siliniyor
- **Dosya:** `SyncOrchestratorService.cs:45, 52`; `OutboxService.cs:205-214, 293-302`; `OfflineResilienceSettings.cs:31`.
- **Neden sorun:** `maxRetries: 3` sabit; `MaxSyncRetries` ölü. Aşan kayıt `MarkStatusUpdateAsSyncedAsync` (=DELETE) ile yok ediliyor; final status kaybı Debug seviyesinde loglanıyor.
- **Düzeltme:** Seçeneği bağlayın; düşürülen kayıtları Warning + `dead` tablosuna taşıyın.

#### M12. `RedisCleanupJob.CleanStaleLocksAsync` TTL'siz her `lock:*` anahtarını siliyor
- **Dosya:** `MilvaionMaintenanceWorker/Jobs/RedisCleanupJob.cs:127-156`.
- **Neden sorun:** Yorum "timestamp varsa kontrol et" diyor ama kod değer varsa doğrudan siliyor (`StaleLockHours` kullanılmıyor). API'nin TTL'siz bir lock'u varsa (ör. uzun süren dispatcher lock'u) aktifken silinir → çift dispatch riski. Güven: orta (API lock TTL'lerine bağlı).
- **Düzeltme:** Lock değerindeki timestamp'i parse edip `StaleLockHours` ile karşılaştırın; parse edilemiyorsa **silmeyin**, loglayın.

#### M13. `AddMilvaionWorkerCore`: Redis yoksa `CancellationListenerService` DI'da patlar; parola escape edilmiyor; ASP.NET Core bağımlılığı
- **Dosya:** `WorkerServiceCollectionExtensions.cs:171-182, 513`; `.csproj`.
- **Neden sorun:** `IConnectionMultiplexer` yalnızca `Redis.ConnectionString` doluysa kayıtlı, `CancellationListenerService` her zaman kayıtlı → boş string'de "Unable to resolve service" ile host açılmaz. Parola `$"{cs},password={pwd}"` ile ekleniyor (virgül/`=` içeren parola bozulur). `WorkerServiceCollectionExtensions` `Microsoft.AspNetCore.Builder`/`WebApplication` kullanıyor ama csproj'da `FrameworkReference` yok — Milvasoft paketleri üzerinden transitif geliyor; konsol worker'ları tüm ASP.NET Core shared framework'ü yüklüyor (Güven: orta, restore edilemedi).
- **Düzeltme:** `IConnectionMultiplexer` opsiyonel (`GetService`) ve Redis yokken listener'ı kaydetmeyin; `ConfigurationOptions.Parse` + `Password` özelliği; health endpoint uzantılarını ayrı `Sdk.Worker.AspNetCore` paketine taşıyın.

### 2.4 Low

- **L1. `HealthCheckOptions` varsayılanları ters:** `ReadyFilePath = "/tmp/live"`, `LiveFilePath = "/tmp/ready"` (`Options/HealthCheckOptions.cs:15-20`). `RabbitMQHealthCheck` `RefreshStatusAsync` ile Redis'i de dahil eden sonucu "RabbitMQ" olarak raporlar; `RedisHealthCheck` async metotta sync `Ping()`; `AddHealthCheckEndpoints` `workerOptions.HealthCheck` null kontrolü yapmıyor.
- **L2. Ölü kod / ölü seçenekler:** `Services/WorkerServices.cs` içindeki `CancellationListener` (kayıtlı değil, farklı mesaj formatı bekliyor), `ConnectionMonitor.OnConnectionRestored`, `HeartbeatSettings.Enabled`, `RedisSettings.Database`, `OfflineResilience.MaxSyncRetries`, `ReporterOptions.DataRetentionDays/LookbackHours`, örnek appsettings'lerdeki `MaxParallelJobsPerWorker` (config'de karşılığı yok, sessizce yok sayılır), `AddMilvaionWorkerWithJobs` adım 6'daki `config.JobType = jobType` (yerel sözlük atılıyor), `JobExecutionResult.Logs`.
- **L3. `Console.WriteLine` kütüphane kodunda:** `WorkerServiceCollectionExtensions` (6+), `JobContext` (log publish hatası), Hangfire/Quartz extension'ları. Yapılandırılmış loglamaya geçin.
- **L4. `ConnectionMonitor`:** Sırf sağlık için 5. RabbitMQ bağlantısı açıyor; başlangıçta `_isRabbitHealthy=false` olduğu için ilk saniyelerde status'lar lokal store'a düşüp 30 s gecikiyor; `Dispose` sync-over-async. Publisher bağlantılarının `IsOpen`/`RecoverySucceededAsync` olaylarını kullanmak daha ucuz ve doğru.
- **L5. Testing paketi boş:** `Milvasoft.Milvaion.Sdk.Worker.Testing.csproj` hiç kaynak içermiyor; `JobTestRunner` `Sdk.Worker` içinde. Paket yalnızca `Logging.Console` transitif bağımlılığı ekliyor — ya `JobTestRunner`'ı buraya taşıyın ya paketi kaldırın. `Sdk.Worker` `LoggerFactory.Create(b => b.AddConsole())` kullanıyor ama `Microsoft.Extensions.Logging.Console`'a doğrudan referansı yok.
- **L6. Reporter worker:** 10 job neredeyse aynı 100 satır (bağlantı, insert, `MetricReport`); `DataSizeBytes = report.Data.Length` karakter sayısı, byte değil; ortak `ReportJobBase` ile 700 satır azalır.
- **L7. Quartz adaptörü:** Kayıt yalnızca `JobAdded→JobScheduled` olay çiftinde; cluster'da başka node'un eklediği veya ADO store'da önceden var olan job'lar kaydedilmez ve `SchedulerStarted` "No Quartz jobs found" deyip heartbeat'i hiç başlatmaz. Quartz 6/7 alanlı cron doğrudan `CronExpression` olarak gönderiliyor (API Cronos ile saniyeyi destekliyorsa sorun yok — doğrulanmadı). `JobDataMap` içeriği (gizli olabilir) registration mesajına serileştiriliyor.
- **L8. EmailWorker:** `UseSsl=true` → STARTTLS, port 465 → implicit TLS (isim yanıltıcı); `FromEmail` job datasından override edilebiliyor (spoofing, tasarım kararı olmalı); `throw new Exception(result.ErrorMessage)` orijinal istisnayı kaybediyor; transient tespiti string eşleştirme. MimeKit header/adres doğrulaması sayesinde header injection pratikte engelleniyor.
- **L9. `MaintenanceWorker.DatabaseMaintenanceJob`** tablo adlarını config'den string interpolasyonla SQL'e gömüyor (`VACUUM "{table}"`); config güvenilir kabul ediliyor ama `information_schema` ile doğrulamak ucuz.
- **L10. `WorkerOptions.MaxParallelJobs<=0`** doğrulanmıyor → `SemaphoreSlim` ctor `ArgumentOutOfRangeException` ile runtime'da patlar; `MaxParallelJobs=65535` → `(ushort)(65535+1)=0` dispatch concurrency. `IValidateOptions<WorkerOptions>` + `ValidateOnStart` ekleyin.
- **L11. Dokümantasyon uyumsuzlukları:** 08-reliability "Worker NACKs the message (no requeue) → waits → republishes" (kod ACK'liyor; NACK olsaydı DLQ'ya giderdi); "Stop accepting new messages → … → ACK all completed jobs" (bkz. H5); "cancelled and marked as TimedOut" (bkz. M5); 05-implementing-jobs "Jobs are created per-execution (scoped)" doğru ama tüm job'ların instantiate edildiği söylenmiyor (M6). 14/15 dokümanlarında güvenlik bölümü yok (H7/H8).
- **L12. DLQ'ya NACK ile düşen ham mesajlar:** JSON parse hatası ve "IJob bulunamadı" yollarında DLX üzerinden kuyruğa `ScheduledJob` gövdesi düşüyor; `FailedOccurrenceHandler` bunu `DlqJobMessage` olarak okuyunca `Status=Queued`, `Exception=null` olur; failure type yanlış sınıflandırılır. Bu yollarda da `PublishToDLQAsync` kullanın.
- **L13. Template:** `content/ApiWorker/.vs/` klasörü repoda (pack'ten hariç ama gereksiz); `appsettings.json`'da `MaxParallelJobsPerWorker: null`. Onun dışında template `AddMilvaionWorkerWithJobs`, `IAsyncJob<TData>`, `JobTestRunner` ile güncel SDK'ya uyumlu.

---

## 3. İyileştirme Önerileri (öncelik sırasıyla)

1. **Outbox'ı doğru çalışır hale getirin (C1, H1, M2, M11):** finalize filtresini kaldırın, OutboxService'i her zaman kaydedin, log flush hatalarını lokal store'a yönlendirin, düşürülen kayıtları görünür kılın. Bu katman için RabbitMQ'yu durdurup/başlatan bir entegrasyon testi ekleyin (Testcontainers).
2. **Tek bir dayanıklı publisher altyapısı (H2, H3, M8):** `RabbitMqPublisherBase` (connection+channel yaşam döngüsü, `AutomaticRecovery`, kanal kilidi, `Persistent`, yeniden açma) yazıp `StatusUpdatePublisher`, `LogPublisher`, `WorkerListenerPublisher`, `ExternalJobPublisher`'ı ondan türetin. Bağlantı sayısını (şu an job tipi başına 1 + 4) tek connection/çoklu channel'a indirin.
3. **Shutdown ve zaman aşımı semantiğini netleştirin (H5, M5, H6):** consumer cancel → job iptal → bekle → kanal kapat; sert timeout; gecikmeli retry için TTL+DLX kuyruğu. Sonra 08-reliability'yi koda göre güncelleyin.
4. **Job çağrı yolu (H4, M1, M6):** reflection yerine tip-güvenli invoker, keyed DI, case-insensitive config eşleşmesi, `IValidateOptions`.
5. **Güvenlik korkulukları (H7, H8):** HttpWorker için hedef allow/deny listesi ve TLS/sertifika seçeneklerini yapılandırmayla kilitleme; SqlWorker için read-only/limit seçenekleri; tüm örnek `appsettings.json`'lardan gerçek parolaları çıkarıp `${ENV}` referansına çevirin; dokümanlara "Security considerations" bölümü.
6. **Bakım job'ları (H9, M12):** `OccurrenceArchiveJob`'ı EF şemasından türetip transaction içine alın; Redis lock temizliğini timestamp'e bağlayın; bu worker'a gerçek DB ile çalışan entegrasyon testi ekleyin.
7. **API yüzeyi ve sürümleme (M9):** Wire DTO'su + `SchemaVersion`; `IJobContext.Job` yerine `IJobContext.JobInfo`; `Sdk.Worker`'dan ASP.NET Core bağımlılığını ayırın; boş Testing paketini anlamlı hale getirin.
8. **Temizlik (L2, L3, L6):** ölü seçenek/kodları kaldırın, `Console.WriteLine`'ları logger'a çevirin, Reporter job'larını ortak tabana indirgeyin, Hangfire/Quartz publisher kopyasını birleştirin.

---

# BÖLÜM E — UI, DevOps & Testler


Kapsam: `src/MilvaionUI`, DevOps dosyaları (docker-compose, Dockerfile'lar, `.github/workflows`, `build/`, keycloak/ad/prometheus/grafana), `tests/`. Repo salt okunur incelendi; testler çalıştırılmadı. Bulgular "Doğrulandı" (kodda birebir görüldü) ya da "Yüksek/Orta güven" (davranış çıkarımı) olarak işaretlendi.

---

## 1. UI (src/MilvaionUI)

### 1.1 Genel bakış ve kalite

- React 18 + Vite 5 + React Router 6, plain CSS, `@microsoft/signalr`, `oidc-client-ts`, `reactflow`, `recharts`, `moment`, `cronstrue`. ~50 sayfa/bileşen, lazy-loaded route'lar, manuel chunk ayrımı (`vite.config.js`) — bundle stratejisi düşünülmüş.
- Kod kalitesi genel olarak iyi: `JsonView` gibi bileşenlerde eski `dangerouslySetInnerHTML`/regex-highlight yaklaşımı kaldırılmış, tüm JSON/log çıktıları React element olarak render ediliyor → **XSS sink bulunmadı** (`dangerouslySetInnerHTML`, `innerHTML`, `eval` grep'i temiz).
- `useEffect` temizlikleri (interval/observer/listener) sistematik olarak var; `useInfiniteScroll`, `useLocalStorageState` gibi hook'lar tekrarı azaltıyor. `LogList` polling'i ref üzerinden stale closure'ı önlüyor.
- Zayıf noktalar: auth token yönetimi (localStorage), SignalR hub'ının kimlik doğrulamasız olması (backend), OIDC silent-renew ile uygulama token state'inin kopuk olması, ~2.3k satır ölü kod, `eslint` script'inin flat config ile çalışmaması, PWA `dev-dist` ve API cache'i.
- Yetki kontrolü UI'da yok: menü/route'lar yetkiye göre gizlenmiyor; her şey backend'e bırakılmış (kabul edilebilir ama UX açısından 403'ler görülecek). Backend'de permission attribute'ları var, o yüzden bu bir güvenlik açığı değil.

### 1.2 Bulgular

#### HIGH

**UI-H1 — SignalR hub kimlik doğrulaması yok; UI de token göndermiyor** (Doğrulandı, cross-scope: backend)
- `src/Milvaion.Api/Hubs/JobsHub.cs:9` — `public class JobsHub : Hub` üzerinde `[Authorize]` yok; `Program.cs:146` `app.MapHub<JobsHub>("/hubs/jobs")` `RequireAuthorization()` olmadan.
- `src/MilvaionUI/src/services/signalRService.js:38` — `.withUrl(hubUrl, { skipNegotiation:false, transport: ... })` → `accessTokenFactory` yok.
- Neden: Anonim herhangi bir istemci `SubscribeToOccurrence(<guid>)` çağırıp `OccurrenceUpdated`/`OccurrenceLogAdded` (job logları, sonuç payload'ları) alabilir. Loglar hassas veri içerebilir (SQL worker sonuçları, HTTP worker yanıtları).
- Düzeltme: Hub'a `[Authorize]`, `MapHub(...).RequireAuthorization()`, JWT `OnMessageReceived` içinde `access_token` query-string desteği; UI'da `accessTokenFactory: () => authService.getAccessToken()`. Ek olarak occurrence bazlı yetki kontrolü (kullanıcının o job'u görme izni var mı).

**UI-H2 — Access + refresh token `localStorage`'da** (Doğrulandı)
- `src/MilvaionUI/src/services/authService.js:114-115`, `pages/Auth/AuthCallback.jsx:32-35`.
- Neden: Herhangi bir XSS (3rd-party script, Google Fonts CDN'i, gelecekte eklenecek bir kütüphane) hem access hem refresh token'ı çalabilir; refresh token uzun ömürlüdür. Bugün XSS sink'i bulunmasa da mimari olarak riskli.
- Düzeltme: Refresh token'ı httpOnly+SameSite cookie'ye taşımak (backend `/account/login/refresh` cookie'den okur), access token'ı memory'de tutup sayfa yenilemede sessizce refresh etmek. En azından refresh token'ı `sessionStorage`'a almak.

**UI-H3 — OIDC silent renew ile uygulama token'ı senkron değil; SSO kullanıcıları için refresh akışı kırık** (Yüksek güven)
- `services/oidcClient.js:33` `automaticSilentRenew: true`, ama `manager.events.addUserLoaded` hiçbir yerde kayıtlı değil (grep boş). `AuthCallback.jsx:32-35` OIDC `access_token`/`refresh_token`'ı bir kez `localStorage`'a yazıyor.
- `services/api.js:78-118` 419 alınca `authService.refreshToken()` → `/account/login/refresh` (`userName`, `refreshToken`, `deviceId`) çağırıyor; OIDC refresh token'ı backend'in beklediği format değil.
- Sonuç: SSO ile giren kullanıcı IdP access token süresi dolunca (tipik 5 dk) 419/401 alır, refresh başarısız olur, login'e atılır; `automaticSilentRenew` yenilediği token'ı kimse okumaz.
- Düzeltme: `manager.events.addUserLoaded(u => authService.setTokens(u.access_token, ...))`; API interceptor'da OIDC oturumu için `manager.signinSilent()` kullanmak; ya da (daha temiz) OIDC callback'te backend'den Milvaion JWT'si takas etmek.

#### MEDIUM

**UI-M1 — PWA runtime cache, yetkili API yanıtlarını Cache Storage'da tutuyor** (Doğrulandı)
- `vite.config.js:90-99` — `urlPattern: /\/api\/.*/i`, `NetworkFirst`, 5 dk / 50 kayıt.
- Neden: `/api/v1/users`, `/api/v1/apikeys`, occurrence logları gibi GET yanıtları logout sonrasında da Cache Storage'da kalır (paylaşılan makine). Ağ 10 sn içinde dönmezse 5 dk'ya kadar bayat veri gösterilir; dashboard için yanıltıcı.
- Düzeltme: `/api/` için runtimeCaching'i kaldırmak (ya da `NetworkOnly`), logout'ta `caches.delete('api-cache')`.

**UI-M2 — `npm run lint` ESLint flat config ile çalışmaz; CI'da UI lint/build adımı yok** (Doğrulandı)
- `package.json:9` — `eslint . --ext js,jsx ...`; `eslint.config.js` flat config. ESLint 8.57 flat config modunda `--ext` desteklenmez ("Invalid option '--ext'"). Ayrıca `globals` ve `@eslint/js` `devDependencies`'te yok (transitive bağımlılığa güveniliyor); `prop-types` 11 dosyada import ediliyor ama `dependencies`'te yok.
- `.github/workflows/ci.yml` UI için hiçbir adım içermiyor (build/lint/audit). UI hatası ancak Docker build'de görülür.
- Düzeltme: `"lint": "eslint ."`; `globals`, `@eslint/js`, `prop-types` açıkça eklenmeli; CI'ya `npm ci && npm run lint && npm run build` job'ı.

**UI-M3 — ~2.3k satır ölü/duplike kod, biri build'de olmayan bağımlılığa (`antd`) referans veriyor** (Doğrulandı)
- Unreferenced: `pages/Dashboard.jsx` (421), `pages/Configuration.jsx` (682), `pages/Admin/AdminDashboard.jsx` (473), `components/JobFormDialog.jsx` (219, `import ... from 'antd'` — paket yok), `components/LoadingSpinner.jsx`, `components/StatusBadge.jsx`, `utils/validators.js`; `pages/Dashboard.css` ve `pages/Configuration.css` de eski sayfalara ait. `App.jsx:22-30` yorumları "switch back is one line" diyor ama eski sayfalar artık redesign'lı olanlarla drift ediyor.
- Düzeltme: Silmek (git geçmişi yeterli). `antd` referansı yanlışlıkla import edilirse build kırılır.

**UI-M4 — `dev-dist/` (service worker çıktısı) repoya commit edilmiş** (Doğrulandı)
- `src/MilvaionUI/dev-dist/{sw.js,workbox-21a80088.js,registerSW.js}`; ne kök `.gitignore` ne `src/MilvaionUI/.gitignore` içeriyor. `devOptions.enabled=false` olduğundan artık üretilmiyor ama kalıntı duruyor.
- Düzeltme: sil + `.gitignore`'a `dev-dist`.

**UI-M5 — Base path altında kırılan mutlak asset yolları** (Doğrulandı)
- `components/Layout.jsx:118` `<img src="/logo.png">` — `/milvaion` altında 404. `index.html:13` `%BASE_URL%logo.svg` — `public/` içinde `logo.svg` yok (`favicon.svg` var) → 404.
- Düzeltme: `import logo from '/logo.png'` ya da `${import.meta.env.BASE_URL}logo.png`; `logo.svg` → `favicon.svg`.

**UI-M6 — Login sonrası `state.from` yok sayılıyor; 401'de her durumda hard redirect** (Doğrulandı)
- `ProtectedRoute.jsx:11` `state={{ from: location }}` set ediyor; `Login.jsx:75` `navigate('/')`. Derin link (`/occurrences/<id>`) kaybolur.
- `api.js:66-72` her 401'de `window.location.href = loginPath` — SPA state kaybı, ayrıca `basename` hesaba katılsa da React Router dışı navigasyon.
- Düzeltme: `navigate(location.state?.from?.pathname ?? '/dashboard')`; 401'i event/callback ile router'a iletmek.

#### LOW

**UI-L1 — `signalRService.disconnect()` hiç çağrılmıyor; logout sonrası bağlantı açık kalıyor** (Doğrulandı) — `Layout.jsx:83 handleLogout` yalnızca `authService.logout()`. Hub anonim olduğu için güvenlik etkisi yok; ama UI-H1 düzeltildiğinde eski token'la bağlı kalır. Logout'ta `disconnect()` eklenmeli.

**UI-L2 — `JobDetail.jsx:68,77` doğrudan `signalRService.invoke('SubscribeToOccurrence')`** kullanıyor; `subscribedOccurrences` set'i güncellenmediğinden `onreconnected → _resubscribeAll` bu abonelikleri geri kurmaz. `subscribeToOccurrence()` kullanılmalı.

**UI-L3 — `signalRService.connect()` içindeki `isConnecting` bekleme döngüsü** (`signalRService.js:19-27`) 100 ms `setInterval` ile busy-wait; promise tabanlı tek bir `connectPromise` daha temiz.

**UI-L4 — Otomatik SW güncellemesi 5 sn sonra zorla reload** (`main.jsx:24-27` `updateSW(true)`) — form dolduran kullanıcı veri kaybeder. Kullanıcıya "yeni sürüm var" toast'ı gösterip onayla reload.

**UI-L5 — Kullanılmayan auth yardımcıları**: `authService.isTokenExpired`, `shouldRefreshToken` hiçbir yerde çağrılmıyor; proaktif refresh yok, sadece 419 reaktif. `Web.config` (`targetFramework 4.8`, `system.web`) Vite SPA için anlamsız kalıntı; IIS'te sadece rewrite kuralı gerekir.

**UI-L6 — Bağımlılık hijyeni**: `moment` (deprecated, 300 KB) yalnızca tarih formatlama için; `dayjs`/`date-fns` ya da `Intl.DateTimeFormat`. `package.json` `"name": "milvaion-demo-app"`, `version 1.0.0` sabit. Dependabot/renovate yapılandırması yok (`.github/dependabot.yml` yok).

**UI-L7 — Erişilebilirlik**: 113 tablo/başlık hücresine karşılık yalnızca 58 `aria-*/role` kullanımı; ikon-only butonlar `title` ile ama `aria-label` çoğunda yok (`Layout.jsx` sidebar-toggle, theme-toggle). 25 yerde `key={index}` (listeler yeniden sıralanınca hatalı diff). Modal'larda focus trap görülmedi.

**UI-L8 — `.env` commit edilmiş** (`VITE_API_BASE_URL=http://localhost:5000/api/v1`) ve `.env.example` ile uyumsuz (`/api` vs `/api/v1`). Kök `.dockerignore` `**/.env`'i dışlıyor, ama `.env.production` build'e girip `VITE_BASE_PATH=/milvaion` diyor; Dockerfile `ENV VITE_BASE_PATH=/__MILVAION_BASE__` ile eziyor (loadEnv'de process.env öncelikli) — doğru çalışıyor ama kırılgan; `Dockerfile-arm` bu ENV'i içermiyor (bkz. DevOps).

### 1.3 Öneriler
1. UI-H1'i backend ile birlikte kapatın (hub authorize + accessTokenFactory).
2. Refresh token'ı httpOnly cookie'ye taşıyın; OIDC için `addUserLoaded` ile token senkronu.
3. CI'ya UI job'ı (lint/build/`npm audit --audit-level=high`) ekleyin; lint script'ini düzeltin.
4. Ölü sayfaları ve `dev-dist`'i silin; `moment`'ı kaldırın.
5. API runtime cache'i kapatın; logout'ta Cache Storage temizleyin.

---

## 2. DevOps

### 2.1 Genel bakış ve kalite

- Dockerfile'lar multi-stage, non-root kullanıcı (`milvaion`), HEALTHCHECK, `provenance`+`sbom` ile push — `.github/DOCKER_SECURITY.md`'de anlatılan iyileştirmeler gerçekten uygulanmış.
- CI: build → unit → integration (Testcontainers) → codecov; TRX raporları. Docker image'ları multi-arch (`amd64` + `arm64`) digest-merge ile.
- Sorunlar: docker-compose'un tüm servisleri sabit şifrelerle host'a açması, API Dockerfile'ın artık var olmayan `*.sln`'e bağımlı olması, NuGet paketlerinin PR'da publish edilmesi, Docker/NuGet publish'in test gate'i olmaması, 8 adet birebir kopya worker workflow'u, `build/scripts` içindeki path bug'ı, `Dockerfile-arm` drift'i, HEALTHCHECK'in yanlış endpoint'i.

### 2.2 Bulgular

#### CRITICAL

**DO-C1 — `sdk-nuget-publish.yml` ve `worker-template-nuget-publish.yml` `pull_request` tetikleyicisiyle NuGet'e push ediyor** (Doğrulandı)
- `.github/workflows/sdk-nuget-publish.yml:9-13`, `worker-template-nuget-publish.yml:9-13`: `on: pull_request: branches: [master] paths: src/Sdk/**` → `dotnet nuget push ... --api-key ${{ secrets.NUGET_API_KEY }}`.
- Neden: Aynı repodan açılan (fork olmayan) bir PR, review'dan önce paketi nuget.org'a yayınlar; `--skip-duplicate` sadece aynı versiyonu engeller, PR'da version bump varsa gider. Fork PR'larında secret gelmez ve job kırmızı olur (gürültü). Publish adımında hiçbir test çalışmıyor.
- Düzeltme: `pull_request` tetikleyicisini kaldırıp PR için yalnızca `pack` (push'suz) job'ı; publish'i `release.yml`/tag'e bağlayıp `needs: [unit-tests, integration-tests]` ile gate'leyin; environment protection kullanın.

#### HIGH

**DO-H1 — API Dockerfile `COPY *.sln .` ama repoda `.sln` yok (yalnızca `Milvaion.slnx`)** (Doğrulandı; build davranışı yüksek güven)
- `src/Milvaion.Api/Dockerfile:54`, `Dockerfile-arm:48`. Kök dizinde `find -name "*.sln"` boş; `Milvaion.slnx` 10 Ağustos tarihli, `docs/githubdocs/DEVELOPMENT.md:191` hâlâ `Milvaion.sln` diyor.
- Neden: Docker `COPY` eşleşmeyen wildcard'da "no source files were specified" ile başarısız olur → `api-docker-build.yml` ve `build/scripts/build-api.sh` kırık. (Geliştirici makinesinde lokal, git'e girmemiş bir `.sln` varsa lokal build çalışır ama CI çalışmaz.)
- Düzeltme: Satırı kaldırın (restore zaten `Milvaion.Api.csproj` ile yapılıyor; `.sln` gereksiz). Ayrıca `tests/*.csproj` kopyaları da restore edilmediği için gereksiz.

**DO-H2 — docker-compose.yml: sabit şifreler, tüm altyapı host'a açık, `ASPNETCORE_ENVIRONMENT=Production` ile `root/admin`** (Doğrulandı)
- `docker-compose.yml:9` `POSTGRES_PASSWORD: N4SQp.qW>6?xwWzg` + `:11` `5432:5432` + `listen_addresses=*`; `:236` pgweb (`sosedoff/pgweb`, auth'suz DB browser) `8082` host'ta, connection string URL-encoded aynı şifre; `:106` Seq `ChangeMe123!` `5341`; `:87-90` RabbitMQ `guest/guest` `5672`+`15672`; `:213` Grafana `admin/admin` `3000`; `:128` `MILVAION_ROOT_PASSWORD=admin`; `:122` `ASPNETCORE_ENVIRONMENT=Production`.
- Aynı postgres şifresi `src/Milvaion.Api/appsettings.json:3` içinde de sabit (production default'u), üstelik `Include Error Detail=true` (hata mesajlarında SQL detayları).
- Neden: `README`'nin önerdiği `docker compose up` bir VPS'te çalıştırılırsa DB, RabbitMQ yönetimi, pgweb ve Seq internete açılır. pgweb özellikle tehlikeli: şifresiz tam DB erişimi.
- Düzeltme: `.env` + `${POSTGRES_PASSWORD:?}` zorunlu değişkenler; altyapı portlarını `127.0.0.1:5432:5432` şeklinde bind etmek ya da hiç yayınlamamak; pgweb/seq/grafana'yı `profiles: [debug]` arkasına almak; `appsettings.json`'dan şifreyi çıkarıp yalnızca `appsettings.Development.json`'da tutmak; `Include Error Detail`'ı sadece Development'ta.

**DO-H3 — Docker image ve NuGet publish'i CI test sonuçlarına bağlı değil; release workflow'u hiçbir şey build etmiyor** (Doğrulandı)
- `api-docker-build.yml` ve 8 worker workflow'u `on: push: branches: [master]` ile bağımsız çalışıyor; `ci.yml` ile `needs`/`workflow_run` ilişkisi yok. `release.yml` yalnızca changelog + GitHub Release oluşturuyor; image/paket versiyonları `VERSION` dosyalarından okunuyor, tag ile tutarlılık kontrolü yok.
- Neden: Testi kırık bir commit master'a girince `:latest` image'ı ve NuGet paketi yine yayınlanır.
- Düzeltme: Tek bir `publish.yml`'de `workflow_run: [CI]` + `conclusion == success` koşulu ya da CI'nın sonuna publish job'ları; tag → VERSION eşitliği doğrulaması.

**DO-H4 — `build/scripts/*.sh` yanlış kök dizin hesaplıyor → tüm build script'leri kırık** (Doğrulandı)
- `build/scripts/build-api.sh:48-50`: `SCRIPT_DIR=.../build/scripts`, `ROOT_DIR="$(dirname "$SCRIPT_DIR")"` = `.../build`, `DOCKERFILE="$ROOT_DIR/src/Milvaion.Api/Dockerfile"` → `build/src/...` yok. Aynı hata `build-worker.sh:49`, `build-email-worker.sh:76`, `build-http-worker.sh:76`, `build-maintenance-worker.sh:76`, `build-sql-worker.sh:76`, `.bat` dosyalarında `%ScriptDir%..`.
- Script'ler `build/` → `build/scripts/` altına taşınmış (`Milvaion.slnx:18-45` hâlâ `build/build-api.sh` yollarını listeliyor — solution'daki referanslar da kırık).
- Düzeltme: `ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"`; `.slnx` yollarını güncelleyin.

#### MEDIUM

**DO-M1 — 8 worker docker workflow'u + 4 NuGet job'ı birebir kopya** (Doğrulandı: `diff httpworker sqlworker` yalnızca isim/yol farkı)
- Değişiklik 9 dosyaya uygulanmak zorunda; `sdk-nuget-publish.yml` 4 job aynı 5 adım.
- Düzeltme: `workflow_call` ile reusable `docker-image.yml` (`inputs: context, dockerfile, image, version_file`) ve caller'larda `strategy.matrix`; NuGet için `matrix.project`.

**DO-M2 — GitHub Actions `permissions:` tanımlı değil, action'lar SHA ile pin'li değil** (Doğrulandı)
- Yalnızca `release.yml:23` `contents: write` tanımlıyor; `ci.yml` ve docker workflow'ları repo default'una (çoğu eski repoda read/write) güveniyor. `dorny/test-reporter@v1` `checks: write` ister; fork PR'larında yazamaz → `if: always()` ile job kırmızı olur. Tüm `uses:` major tag (`@v4`) ile, SHA pin yok; `dorny/test-reporter`, `softprops/action-gh-release`, `codecov/codecov-action` üçüncü taraf.
- Düzeltme: Workflow başına `permissions: contents: read` + job bazlı yükseltme; `pull_request` için test-reporter'ı `pull_request_target`/`workflow_run` desenine almak ya da atlamak; action'ları SHA'ya pin'leyip Dependabot `github-actions` ekosistemi ile güncellemek.

**DO-M3 — API HEALTHCHECK var olmayan endpoint'e gidiyor; SPA fallback yüzünden her zaman "healthy"** (Doğrulandı)
- `src/Milvaion.Api/Dockerfile:98-99` `curl -f http://localhost:5000/api/health`. Gerçek route `api/v{version}/healthcheck` (`HealthCheckController` + `GlobalConstant.FullRoute`), `/live`,`/ready` alt yolları. `/api/health` `MapFallback("{*path:nonfile}")` ile `index.html` (200) döner → healthcheck anlamsız.
- Worker HEALTHCHECK'leri `pgrep -f "dotnet.*HttpWorker"` — proses yaşıyorsa healthy; RabbitMQ bağlantısı kopuk olsa da fark etmez (SDK'da `FileHealthCheckBackgroundService` var, kullanılmıyor).
- Düzeltme: `/api/v1/healthcheck/live`; worker'larda file-based health check dosyasını kontrol etmek.

**DO-M4 — `Dockerfile-arm` kullanılmıyor ve drift etmiş** (Doğrulandı)
- Workflow'lar `platforms: linux/arm64` ile ana `Dockerfile`'ı kullanıyor; `Dockerfile-arm` `ENV VITE_BASE_PATH=/__MILVAION_BASE__` satırını içermiyor → bu dosyayla build edilen image `.env.production`'dan `/milvaion` base path'ini sabitler ve runtime `MilvaionConfig:BasePath` ile çalışmaz. Ayrıca ana Dockerfile `package.json` kopyalayıp `npm install --legacy-peer-deps` (lock yok, reprodüksiyon yok) kullanırken `-arm` `npm ci` kullanıyor — doğru olan `-arm`'dakidir.
- Düzeltme: `Dockerfile-arm`'ı silin; ana Dockerfile'da `COPY package.json package-lock.json ./` + `npm ci`.

**DO-M5 — Prometheus `--web.enable-lifecycle` + host'a açık 9090** (Doğrulandı `docker-compose.yml:196`)
- Auth'suz `POST /-/quit` ve `/-/reload` mümkün. `prometheus.yml` `environment: 'production'` label'ı ve API `/api/metrics` scrape'i auth'suz (metrics endpoint'inin auth'u backend kapsamı).
- Düzeltme: flag'i kaldırın; portu bind etmeyin (Grafana iç ağdan erişir).

**DO-M6 — `:latest` image tag'leri ve pin'siz base image'lar** (Doğrulandı)
- `docker-compose.yml`: `datalust/seq:latest`, `prom/prometheus:latest`, `grafana/grafana:latest`, `sosedoff/pgweb:latest`, `nowsci/samba-domain:latest` (samba-ad, `privileged: true`). Testcontainers'ta `postgres:latest` (`CustomWebApplicationFactory.cs:24`) — üretim `postgres:16-alpine` iken testler farklı major sürümde koşabilir.
- Düzeltme: Sürüm (tercihen digest) pin'i; Testcontainers'ta `postgres:16-alpine`.

**DO-M7 — `ci.yml` integration job'ında tanımlanan `services:` (postgres/redis/rabbitmq) kullanılmıyor** (Doğrulandı)
- Testler Testcontainers ile kendi container'larını başlatıyor (`CustomWebApplicationFactory`), env var'lar `ConnectionStrings__DefaultConnectionString` fixture tarafından eziliyor; `RabbitMQ__Host` anahtarı da uygulamanın okuduğu `MilvaionConfig__RabbitMQ__Host` değil. Servisler boşuna ayağa kalkıyor, runner'da port çakışması riski (5432/6379/5672 host'ta doluyken Testcontainers rastgele port kullandığı için çalışır ama kafa karıştırıcı).
- Düzeltme: `services:` bloğunu ve env'leri kaldırın.

**DO-M8 — Keycloak realm dev fixture'ı `sslRequired: none`, `test/test` admin kullanıcısı** (Doğrulandı `keycloak/realm-milvaion.json:4,13`)
- Sadece localhost redirect URI'ları var, dev amaçlı olduğu açık; ancak dosya adı `realm-milvaion.json` ve README'de "import this realm" deniyorsa üretime taşınma riski. `ad/README.md` ve `docker-compose.samba-ad.yml` de `AdminP@ss123`, `INSECURELDAP=true` içeriyor (dev, kabul edilebilir; ama `privileged: true` container).
- Düzeltme: Dosya adını `realm-milvaion.dev.json` yapın, başına açık uyarı; `docker-compose.samba-ad.yml`'i `dev/` altına.

#### LOW

**DO-L1 — `postgresql.conf` compose tarafından mount edilmiyor** — aynı ayarlar `command: -c ...` olarak tekrar edilmiş (`docker-compose.yml:14-46`); 8 GB varsayımı (`shared_buffers=2GB`, `effective_cache_size=6GB`) `mem_limit: 12096m` ile tutarlı ama küçük makinelerde başlangıçta OOM. Ya dosyayı mount edin ya silin.

**DO-L2 — `quick-start.sh/.ps1` bayat**: `http://localhost:5000/swagger` (Scalar kullanılıyor), `src/Milvaion.Api/Migrations/002_CreateJobOccurrencesTable.sql` (EF migrations var, SQL dosyası yok), `POST /api/v1/jobs/job` (auth'suz çalışmaz). `trap cleanup EXIT` `logs -f`'ten sonra → Ctrl+C tüm servisleri `down` eder.

**DO-L3 — `.dockerignore`**: `**/docker-compose*` ve `**/Dockerfile*` dışlanıyor (iyi) ama `**/.env` yalnızca tam `.env` ile eşleşir; `tests/` ve `docs/` dışlanmadığından build context gereksiz büyük (`coverage.json` 115 KB dahil). `Milvaion.UnitTests.csproj.Backup.tmp` ve `tests/Milvaion.IntegrationTests/coverage.json` repoya girmiş (`.gitignore`'a `*.Backup.tmp`, `coverage.json`).

**DO-L4 — `sdk-nuget-publish.yml` `dotnet-version: ${{ vars.DOTNET_VERSION }}`** repo variable'a bağlı, `ci.yml` `10.0.x` env ile; `global.json` yok. Variable silinirse setup-dotnet boş versiyonla çalışır. `dotnet-tools.json` boş (`"tools": {}`) — ya EF tool ekleyin ya silin.

**DO-L5 — Grafana `GF_SERVER_ROOT_URL=http://localhost:3000`** Vite dev server (`3000`) ile çakışır; UI README `npm run dev` → 3000. Grafana'yı 3001'e alın. Grafana dashboards `editable: true` + provisioning `disableDeletion: false` — sağlanan dashboard'lar UI'dan değiştirilip restart'ta ezilir.

**DO-L6 — `.editorconfig` `end_of_line = crlf`** (C#), `.gitattributes` ile birlikte Linux CI'da EF migration/`dotnet format` diff gürültüsü; `insert_final_newline = false`. Kod tabanı Linux container'da build ediliyor; `lf` tercih edilir.

**DO-L7 — `codecov.yml` `patch: target 80%`** ama `code-coverage` job'ı yalnızca `push` + `master`'da çalışıyor (`ci.yml:236`) → PR'larda patch coverage hiç hesaplanmıyor; `fail_ci_if_error: false`. Codecov'a PR'da da yükleyin.

### 2.3 Öneriler
1. NuGet publish'i PR'dan derhal kaldırın (DO-C1); publish'leri CI'ya gate'leyin (DO-H3).
2. `COPY *.sln` satırını silin; `npm ci`; HEALTHCHECK yolunu düzeltin.
3. Compose'u `.env`-driven yapın, altyapı portlarını loopback'e bağlayın, pgweb/seq/grafana'yı profile'a alın.
4. Worker workflow'larını reusable workflow + matrix'e indirgeyin; `permissions` ve SHA pin ekleyin; Dependabot (`nuget`, `npm`, `github-actions`, `docker`).
5. `build/scripts` path bug'ını düzeltin ya da script'leri tamamen `docker buildx bake` ile değiştirin.

---

## 3. Testler (tests/)

### 3.1 Genel bakış ve kalite

- 3 proje: `Milvaion.UnitTests` (85 dosya, ~1.150 test), `Milvaion.IntegrationTests` (64 dosya, ~500 test), `SampleWorker.Tests` (7 test). xUnit + FluentAssertions 7 + Moq; integration tarafında Testcontainers (Postgres/Redis/RabbitMQ) + Respawn + WireMock.
- Olumlu: Assert'siz test yok (1.671 test metodunun tamamında `Should()`/`Verify` var). Testler AAA düzeninde, isimler açıklayıcı, birçok testte "neden" yorumu var. Respawn ile her testte DB reset; koleksiyon bazlı izole DB (`testDb`, `testDb_services`) ile paralel koşum. Kritik background service'ler (JobDispatcher 24, StatusTracker 21, WorkflowEngine 22+19 unit, FailedOccurrenceHandler, Zombie, LogCollector, ExternalJobTracker) gerçek Redis/RabbitMQ/Postgres ile integration test edilmiş. SDK consumer ack/nack/DLQ/retry (`JobConsumerTests`: ShouldAckMessage, ShouldNackBadMessage_AndRouteToDLQ, ShouldRetryFailedJob, ShouldMoveToDLQ_WhenMaxRetriesExceeded, ShouldSkipRetryAndDLQ_WhenPermanentFailure) kapsanmış. Cron projeksiyonu (`CronProjectorTests`, `StartupScheduleResolverTests`) var.
- Zayıf: Yetki (403) testi yok, API key auth testi yok, LDAP/OIDC testi yok, MCP tools testi yok, Application handler'larının çoğu yalnızca controller üzerinden dolaylı test ediliyor, zaman bazlı senkronizasyon `Task.Delay` (257 kullanım, 107'si ≥1 sn) ile → flaky, `StopAsync` `finally` içinde değil → servis sızıntısı, 2 skip'li test, çalışmayan CI servisleri.

### 3.2 Kapsama haritası (src alanı → test)

| Alan | Test | Durum |
|---|---|---|
| `JobDispatcherService` | `IT/BackgroundServices/JobDispatcherServiceTests` (24) | İyi; cron next-run (`:959-962`) dispatcher içinde dolaylı |
| `StatusTrackerService` | `IT/.../StatusTrackerServiceTests` (21) | İyi; 45 `Task.Delay` |
| `WorkflowEngineService` | IT (22, 1 skip) + Unit (19) | İyi; `Disabled_ShouldNotProcess` skip |
| `FailedOccurrenceHandler`, `ZombieOccurrenceDetector`, `LogCollector`, `WorkerAutoDiscovery`, `ExternalJobTracker` | IT | Var |
| `JobMisfireMonitorService`, `ApiKeyExpiryMonitorService`, `InactiveExternalUserCleanupService` | — | **Yok** |
| Cron / next execution | `CronProjectorTests` (7), `StartupScheduleResolverTests` (8), validator testleri | Kısmi: `CreateScheduledJobCommandHandler:35-37`, `UpdateScheduledJobCommandHandler:46-48`, `JobDispatcherService:959` içindeki üç ayrı `GetNextOccurrence` kullanımı doğrudan test edilmiyor; DST/UTC sınırları, `GetNextOccurrence == null` (asla çalışmayacak cron) yolu test edilmemiş |
| Auth: login/refresh/logout | `AccountControllerTests` (10) | Var |
| Auth: API key (`ApiKeysController`, key ile istek) | — | **Yok** (grep `X-Api-Key`/`ApiKey` testlerde boş) |
| Auth: LDAP (`LdapAuthenticator`), OIDC (`ExternalIdentityService`) | — | **Yok** |
| Permission enforcement (403) | — | **Yok**: tüm testler `SeedRootUserAndSuperAdminRoleAsync` + `LoginAsync()` ile SuperAdmin; yalnızca anonim → 401 testleri var (`HttpStatusCode.Forbidden` tek yerde, o da `NotBe`) |
| `PermissionManager`, `PermissionCatalog` | `PermissionCatalogTests` (2) | Yetersiz |
| Controllers | Account, ActivityLogs, Admin, Dashboard, Developer, HealthCheck, Jobs (54), Languages, Lookups, MetricReports, Permissions, Roles, Settings (1), UI, Users, Workers, Workflows (25) | `ApiKeysController`, `CmsController`, `ExportsController` **yok** |
| MCP (`Mcp/*Tools.cs`, `McpPermissionGuard`) | — | **Yok** — LLM'e açılan job tetikleme/ops araçları, yetki guard'ı test edilmemiş |
| Alerting | Unit (6 dosya) + IT `AlertNotifierTests` | İyi |
| Redis servisleri (Scheduler, Lock, Stats, Worker, Cancellation, CircuitBreaker) | IT + Unit | İyi |
| RabbitMQ publisher/connection | IT (2 dosya, biri 2 test) + Unit | Orta |
| SDK Worker (JobConsumer, JobExecutor, Outbox, LocalStateStore, publishers, health) | IT (13) + Unit (15) | İyi |
| SDK Quartz / Hangfire | Unit + IT publisher testleri | Orta; Hangfire testleri `Thread.Sleep(500)` |
| `ExportService`, `NotificationService`, `SettingsProvider`, `RabbitMQManagementClient`, `AdminService` (kısmen AdminController), `AccountManager` | — | **Yok / dolaylı** |
| DTO/Entity/Enum/Constants | Unit (ComponentTests, ~40 dosya) | Aşırı: reflection ile property set/get testleri (`ComprehensiveReflectionDtoTests`, `EntityTests` 996 satır) coverage şişiriyor, regresyon değeri düşük |
| UI | — | Yok (codecov'da ignore) |
| `SampleWorker.Tests` | 7 test | `ci.yml`'de çalıştırılmıyor |

### 3.3 Bulgular

#### HIGH

**TS-H1 — Yetkilendirme (permission) hiç test edilmiyor** (Doğrulandı)
- `tests/Milvaion.IntegrationTests/TestBase/TestExtensions.cs:11` `LoginAsync(username="rootuser")`; `IntegrationTestBase.cs:116` yalnızca SuperAdmin seed'i. Controller testlerinde `Forbidden` beklentisi yok.
- Neden: Bir controller'dan `[Authorize(Permission=...)]` düşse ya da `McpPermissionGuard` yanlış çalışsa hiçbir test kırılmaz. Çok kullanıcılı bir scheduler için en riskli boşluk.
- Düzeltme: `SeedUserWithPermissionsAsync(params string[] permissions)` helper'ı; her controller için en az bir "yetkisiz kullanıcı → 403" ve "salt-okur kullanıcı → write endpoint 403" testi; `[Theory]` ile endpoint × permission matrisi.

**TS-H2 — API key auth, LDAP, OIDC ve MCP araçları test edilmemiş** (Doğrulandı: ilgili dosya/grep boş)
- `ApiKeysController`, `X-Api-Key` ile kimlik doğrulama, `LdapAuthenticator`, `ExternalIdentityService`, `Mcp/*` için hiçbir test yok. `docker-compose.samba-ad.yml` ve Keycloak realm'i manuel test için var; otomasyona bağlanmamış.
- Düzeltme: API key için IT (oluştur → key ile istek → revoke → 401); LDAP için Testcontainers `osixia/openldap` ya da Samba container; OIDC için WireMock ile discovery/JWKS mock'u (WireMock.Net zaten referansta); MCP tool'ları için in-process çağrı testleri, özellikle `McpPermissionGuard`.

**TS-H3 — Zaman bazlı senkronizasyon `Task.Delay` ile; CI flakiness kanıtı var** (Doğrulandı)
- 257 `Task.Delay`, 107'si ≥1000 ms (`StatusTrackerServiceTests` 45, `LogCollectorServiceTests` 27, `JobConsumerTests` 26, `WorkflowEngineServiceTests` 24). `WaitForConditionAsync` yalnızca 2 dosyada. `WorkflowEngineServiceTests.cs:1166` `[Fact(Skip = "CI flaky test")]`, `BasePathIntegrationTests.cs:198` `[Fact(Skip = "Failed on Github actions.")]` — ikisi de kök neden çözülmeden kapatılmış. `MilvaionJobFilterTests.cs:63,123,147,187,214,234` `Thread.Sleep(500)`.
- Neden: Runner yavaşladığında sahte kırmızı, hızlı olduğunda gereksiz bekleme (toplam dakikalar). Skip'li test SPA fallback'in base path altında çalıştığını doğrulayan tek test (DO-M3 ile ilişkili).
- Düzeltme: Ortak `WaitUntilAsync(Func<Task<bool>>, timeout, pollInterval)` helper'ı (TestBase'e); servis polling interval'larını test option'larıyla 100 ms'e çekmek; `TimeProvider`/`FakeTimeProvider` enjekte ederek deterministik hale getirmek; skip'leri `Trait("Category","Flaky")` ile ayrı job'da çalıştırıp kök nedeni açmak.

#### MEDIUM

**TS-M1 — Background service `StopAsync` çağrıları `try/finally` içinde değil → başarısız test bir sonrakine servis sızdırıyor** (Doğrulandı)
- `StatusTrackerServiceTests.cs:85,146,...` her testte `await tracker.StopAsync(cts.Token)` assert'lerden sonra; `BackgroundServiceTestBase` `IAsyncDisposable` ile takip etmiyor. `WorkflowEngineServiceTests.cs:1171` yorumu: "any leaked engines from prior tests" — sorun biliniyor.
- Neden: Aynı koleksiyon aynı host + DB'yi paylaşıyor; sızan engine sonraki testin seed'ini işleyip yanlış sonuç üretir → ordering dependency ve kaskad kırmızı.
- Düzeltme: `StartBackgroundServiceAsync` başlatılan servisleri listeye alsın, `DisposeAsync` içinde hepsini `StopAsync` etsin; testlerde `await using var _ = await StartAsync<T>()`.

**TS-M2 — Process-wide static state: `Environment.SetEnvironmentVariable` ve statik `_hostBuildLock`** (Doğrulandı `CustomWebApplicationFactory.cs:62,100-129`)
- Üç fabrika (Milvaion, Services, BasePath) paralel başlıyor; her biri aynı env var'ları (`ConnectionStrings__DefaultConnectionString`, `MilvaionConfig__Redis__ConnectionString`) yazıp kilit altında host build ediyor. Kilit host build'i koruyor ama lazy/late-bound okuma yapan her şey (`IOptionsMonitor`, `IConfiguration` reload, statik initializer) son yazılan fabrikanın değerini görür. `ConfigureWebHost` in-memory override bu yüzden eklenmiş — iki mekanizma yan yana.
- Düzeltme: `Program.cs`'i env var yerine `WebApplicationFactory`'nin `UseSetting`/in-memory config'ini erken okuyacak şekilde düzenlemek (config'i `builder.Configuration`'a `ConfigureAppConfiguration` içinde eklemek) ve env var yazımını kaldırmak.

**TS-M3 — Migration hataları sessizce yutuluyor** (Doğrulandı `IntegrationTestBase.cs:55-67` `catch (Exception) { // Handle exception }`)
- Migration başarısızsa testler "table does not exist" gibi ilgisiz hatalarla düşer. `SetupTestJobQueueAsync` de `catch {}`.
- Düzeltme: `_output.WriteLine(ex)` + rethrow; en azından `Skip`/açık hata.

**TS-M4 — Testler paylaşılan `IServiceScope` ve tracking'li DbContext ile aynı entity'leri seed/assert ediyor** (Orta güven)
- `IntegrationTestBase.GetDbContext()` yeni scope açıyor ama scope'u dispose etmiyor (`scope` değişkeni düşüyor) → her çağrıda scope sızıntısı. `SeedRootUserAndSuperAdminRoleAsync` `Permission { Id = 1 }` sabit Id ile ekliyor; Respawn sequence'leri 5000'e resetlediğinden çakışmıyor ama `ResetAutoIncrementQuery` hem fabrika hem base'de iki kopya.
- Düzeltme: `GetDbContext()` `(scope, ctx)` döndürsün ya da `IAsyncDisposable`; sabit sorguyu tek yerde tutun.

**TS-M5 — Reflection tabanlı DTO/entity testleri coverage'ı şişiriyor** (Doğrulandı)
- `ComponentTests/DtoTests/*` (9 dosya), `EntityTests.cs` (996 satır), `SdkTests/ModelTests`, `WorkerSdkTests/ModelTests`: property get/set, enum değer sayısı, `TableNames` sabitleri. Codecov `patch 80%` hedefi bu testlerle karşılanırken iş mantığı (handler'lar) test edilmeden geçebilir.
- Düzeltme: Bu testleri küçültün; codecov'da `flags` ile `Infrastructure`/`Application` katmanlarına ayrı hedef koyun.

**TS-M6 — `SampleWorker.Tests` CI'da çalışmıyor; `tests/Milvaion.IntegrationTests/coverage.json` ve `Milvaion.UnitTests.csproj.Backup.tmp` commit edilmiş** (Doğrulandı)
- `ci.yml` yalnızca iki test projesini çalıştırıyor; `dotnet test` solution üzerinden çalıştırılsa (`Milvaion.slnx`) üçü de koşar. `coverage.json` (115 KB, kaynak kod içeriyor) ve `.Backup.tmp` gürültü.
- Düzeltme: `dotnet test Milvaion.slnx --filter ...` ya da SampleWorker job'ı; dosyaları silip `.gitignore`'a ekleyin.

#### LOW

**TS-L1 — Testcontainers `postgres:latest`** (`CustomWebApplicationFactory.cs:24`) prod `16-alpine` iken; Postgres 17/18 davranış farkı testleri prod'dan ayrıştırır. Pin'leyin.

**TS-L2 — Unit test projesi `Milvaion.Api`'ye referans veriyor** (`Milvaion.UnitTests.csproj`) ve `Microsoft.AspNetCore.Mvc.Testing`, `OpenTelemetry.Exporter`, `SQLitePCLRaw` gibi integration bağımlılıkları taşıyor → "unit" derlemesi ağır; katman sınırı bulanık.

**TS-L3 — Cron kenar durumları eksik**: `CronProjectorTests` 7 test; `"0 0 30 2 *"` gibi hiç oluşmayan ifade (`GetNextOccurrence` → null → handler'da `!.Value` NRE, `CreateScheduledJobCommandHandler.cs:37`), saniye alanı olmayan 5-alanlı ifade (`CronFormat.IncludeSeconds` ile parse hatası), yıl sonu / artık yıl. Validator testleri bunların bir kısmını yakalıyor olabilir; `[Theory]` ile ifade tablosu önerilir.

**TS-L4 — FluentAssertions `[7.0.0]` sabit pin** (lisans değişikliği nedeniyle bilinçli, iyi) ama `Directory.Packages.props` yok; test paket sürümleri iki csproj'da tekrar ediyor (`10.0.11`, `18.8.1`, `2.9.3`...). Central Package Management önerilir.

**TS-L5 — `coverage.runsettings` `ExcludeByFile` `**/Workers/**/Program.cs`** ile worker'ların ana kablolaması dışarıda; `Mcp/**` ve `Hubs/**` gibi test edilmeyen alanlar dahil (doğru), ancak `[Milvasoft.Templates.Milvaion*]*` exclude'u template projesinin derlenmesine bağlı — template test edilmiyor, `TemplateTest` configuration'ı csproj'larda tanımlı ama CI'da kullanılmıyor.

### 3.4 Risk sıralı kapsama boşluğu listesi
1. Permission/403 enforcement (tüm controller'lar + MCP guard) — **kritik**
2. API key kimlik doğrulama ve `ApiKeysController` yaşam döngüsü — yüksek
3. LDAP / OIDC login yolları (`LdapAuthenticator`, `ExternalIdentityService`, `InactiveExternalUserCleanupService`) — yüksek
4. MCP tool'ları (`MilvaionJobTools`, `MilvaionOpsTools`: job tetikleme/iptal LLM'den) — yüksek
5. `JobMisfireMonitorService`, `ApiKeyExpiryMonitorService` — orta
6. Cron `GetNextOccurrence == null` ve DST/UTC sınır durumları (Create/Update handler'ları) — orta
7. `ExportsController`/`ExportService` (dosya üretimi, path traversal) — orta
8. `CmsController`/ContentManagement — düşük-orta
9. SignalR `JobsHub` (grup abonelik/ayrılma, yetki) — orta (UI-H1 ile birlikte)
10. `RabbitMQManagementClient`/`QueueDepthMonitor` gerçek management API'sine karşı — düşük
11. UI: hiç test yok (en azından `api.js` interceptor/refresh kuyruğu ve `cronBuilder.js` için Vitest) — orta

### 3.5 Öneriler
1. `WaitUntilAsync` + `FakeTimeProvider` ile `Task.Delay`'leri kaldırın; skip'li iki testi geri açın.
2. Yetki matrisi testleri ve API key/LDAP/OIDC/MCP testleri ekleyin (TS-H1/H2).
3. Servis yaşam döngüsünü `IAsyncDisposable` ile bağlayın (TS-M1); env var yerine in-memory config (TS-M2).
4. Reflection DTO testlerini budayın; codecov flag'leri ile katman hedefleri.
5. CI'da `dotnet test Milvaion.slnx`; `services:` bloğunu kaldırın; `coverage.json`/`.Backup.tmp` temizliği.
