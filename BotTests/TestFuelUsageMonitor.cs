using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Channels;
using HambotV2.FuelUsageMonitor;
using HambotV2.Maintenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BotTests
{
    [TestClass]
    public sealed class TestFuelUsageMonitor
    {
        private const string Token = "torque-upload-token";
        private const string SampleQueryString = "?v=8&session=1764012345000&time=1764012346000&eml=driver%40example.com&kc=2140&kd=87.5";

        // ---- settings -------------------------------------------------------------------

        private static IConfiguration Configuration(params (string Key, string Value)[] values)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
                .Build();
        }

        private static FuelUsageMonitorSettings CreateSettings(
            int maxUploadsPerMinute = 120,
            int maxQueryBytes = 8192,
            int maxBodyBytes = 1024 * 1024,
            int queueCapacity = 10)
        {
            return new FuelUsageMonitorSettings
            {
                Enabled = true,
                ListenUri = "http://127.0.0.1:0",
                ListenPath = "/fuel/upload",
                UploadToken = Token,
                MaxUploadsPerMinute = maxUploadsPerMinute,
                MaxQueryBytes = maxQueryBytes,
                MaxBodyBytes = maxBodyBytes,
                QueueCapacity = queueCapacity
            };
        }

        private static FuelUsageRequestGate CreateGate(FuelUsageMonitorSettings settings, TimeProvider? timeProvider = null)
        {
            var limiter = new FuelUsageRateLimiter(settings.MaxUploadsPerMinute, timeProvider ?? new FakeTimeProvider());
            return new FuelUsageRequestGate(settings, limiter);
        }

        [TestMethod]
        public void Settings_Are_Inactive_By_Default()
        {
            FuelUsageMonitorSettings settings = FuelUsageMonitorSettings.FromConfiguration(Configuration());

            Assert.IsFalse(settings.Enabled, "The endpoint is opt-in.");
            Assert.IsFalse(settings.IsActive, "Without enabled + token nothing runs.");
            Assert.AreEqual("http://0.0.0.0:2526", settings.ListenUri);
            Assert.AreEqual("/fuel/upload", settings.ListenPath);
            Assert.AreEqual(120, settings.MaxUploadsPerMinute);
            Assert.AreEqual(8192, settings.MaxQueryBytes);
            Assert.AreEqual(8 * 1024 * 1024, settings.MaxBodyBytes);
            Assert.AreEqual(1000, settings.QueueCapacity);
        }

        [TestMethod]
        public void Settings_Need_A_Token_To_Be_Active()
        {
            FuelUsageMonitorSettings enabledWithoutToken = FuelUsageMonitorSettings.FromConfiguration(
                Configuration(("FuelUsageMonitor:Enabled", "true")));
            FuelUsageMonitorSettings enabledWithToken = FuelUsageMonitorSettings.FromConfiguration(
                Configuration(("FuelUsageMonitor:Enabled", "true"), ("FuelUsageMonitor:UploadToken", Token)));

            Assert.IsTrue(enabledWithoutToken.Enabled);
            Assert.IsFalse(enabledWithoutToken.IsActive, "Enabled without a token is not active.");
            Assert.IsTrue(enabledWithToken.IsActive);
        }

        [TestMethod]
        public void Settings_Fall_Back_On_Invalid_Values_And_Normalize_The_Path()
        {
            FuelUsageMonitorSettings settings = FuelUsageMonitorSettings.FromConfiguration(Configuration(
                ("FuelUsageMonitor:Enabled", "true"),
                ("FuelUsageMonitor:UploadToken", Token),
                ("FuelUsageMonitor:ListenPath", "fuel/upload/"),
                ("FuelUsageMonitor:MaxUploadsPerMinute", "0"),
                ("FuelUsageMonitor:MaxQueryBytes", "12"),
                ("FuelUsageMonitor:MaxBodyBytes", "10"),
                ("FuelUsageMonitor:QueueCapacity", "-5")));

            Assert.AreEqual("/fuel/upload", settings.ListenPath, "A path is normalized to a single leading slash.");
            Assert.AreEqual(120, settings.MaxUploadsPerMinute);
            Assert.AreEqual(8192, settings.MaxQueryBytes, "An absurdly small query bound falls back.");
            Assert.AreEqual(8 * 1024 * 1024, settings.MaxBodyBytes);
            Assert.AreEqual(1000, settings.QueueCapacity);
        }

        [TestMethod]
        public void Settings_Endpoint_Is_The_Listen_Address_Plus_The_Route()
        {
            Assert.AreEqual("http://127.0.0.1:0/fuel/upload", CreateSettings().Endpoint);
            Assert.AreEqual("http://0.0.0.0:2526/fuel/upload", FuelUsageMonitorSettings.FromConfiguration(Configuration()).Endpoint);
        }

        // ---- parser ---------------------------------------------------------------------

        private static List<KeyValuePair<string, string>> Query(params (string Key, string Value)[] parameters)
        {
            return parameters.Select(parameter => new KeyValuePair<string, string>(parameter.Key, parameter.Value)).ToList();
        }

        private static List<KeyValuePair<string, string>> SampleQuery()
        {
            return Query(
                ("v", "8"),
                ("session", "1764012345000"),
                ("id", "80c9065bff55006e9f5f3d4f8d9456ae"),
                ("time", "1764012346000"),
                ("eml", "driver@example.com"),
                ("profileName", "the van"),
                ("userUnits", "1"),
                ("profileFuel", "95"),
                ("kc", "2140"),
                ("kd", "87.5"),
                ("kff1005", "50.08527848"));
        }

        [TestMethod]
        public void Parser_Keeps_The_Drive_Keys_And_Every_Pid()
        {
            TorqueSample? sample = TorqueSampleParser.Parse(SampleQuery());

            Assert.IsNotNull(sample);
            Assert.AreEqual("1764012345000", sample.Session);
            Assert.AreEqual("80c9065bff55006e9f5f3d4f8d9456ae", sample.DeviceId);
            Assert.AreEqual("8", sample.Version);
            Assert.AreEqual("driver@example.com", sample.Email);
            Assert.AreEqual("the van", sample.ProfileName);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1764012346000), sample.TimestampUtc);
            CollectionAssert.AreEquivalent(new[] { "kc", "kd", "kff1005" }, sample.Values.Keys.ToList());
            Assert.AreEqual(87.5, sample.Values["kd"]);
        }

        [TestMethod]
        public void Parser_Requires_A_Drive_Key_And_A_Usable_Time()
        {
            Assert.IsNull(TorqueSampleParser.Parse(Query()), "An empty request is not a sample.");
            Assert.IsNull(TorqueSampleParser.Parse(Query(("time", "1764012346000"))), "No session and no id.");
            Assert.IsNull(TorqueSampleParser.Parse(Query(("session", "1"), ("kc", "900"))), "No time.");
            Assert.IsNull(TorqueSampleParser.Parse(Query(("session", "1"), ("time", "not-a-number"))));
            Assert.IsNull(TorqueSampleParser.Parse(Query(("session", "1"), ("time", "-1"))));
            Assert.IsNull(TorqueSampleParser.Parse(Query(("session", "1"), ("time", "4102444800001"))), "Past 2100 is not a real reading.");
            Assert.IsNotNull(TorqueSampleParser.Parse(Query(("id", "abc"), ("time", "1764012346000"))), "The id alone identifies the drive.");
        }

        [TestMethod]
        public void Parser_Drops_Only_The_Value_It_Can_Not_Read()
        {
            TorqueSample? sample = TorqueSampleParser.Parse(Query(
                ("session", "1"),
                ("time", "1764012346000"),
                ("kc", "2140"),
                ("kbad", "not-a-float"),
                ("kempty", ""),
                ("kzero", "0.0"),
                ("kNaN", "NaN"),
                ("kInf", "Infinity")));

            Assert.IsNotNull(sample);
            CollectionAssert.AreEquivalent(new[] { "kc", "kzero" }, sample.Values.Keys.ToList());
            Assert.AreEqual(0.0, sample.Values["kzero"], "Zero is a real reading.");
        }

        [TestMethod]
        public void Parser_Resolves_A_Repeated_Key_To_The_Last_Occurrence()
        {
            TorqueSample? sample = TorqueSampleParser.Parse(Query(
                ("session", "first"),
                ("session", "second"),
                ("time", "1764012346000"),
                ("time", "1764012347000"),
                ("kd", "10"),
                ("kd", "20")));

            Assert.IsNotNull(sample);
            Assert.AreEqual("second", sample.Session);
            Assert.AreEqual(20, sample.Values["kd"]);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1764012347000), sample.TimestampUtc);
        }

        [TestMethod]
        public void Parser_Bounds_Pid_Count_And_Text_Fields()
        {
            var parameters = new List<KeyValuePair<string, string>>
            {
                new("session", new string('s', 500)),
                new("time", "1764012346000"),
                new("eml", new string('e', 400)),
                new("profileName", new string('p', 200)),
                new("v", new string('v', 40))
            };
            for (int index = 0; index < 200; index++)
                parameters.Add(new KeyValuePair<string, string>($"k{index}", "1"));

            TorqueSample? sample = TorqueSampleParser.Parse(parameters);

            Assert.IsNotNull(sample);
            Assert.AreEqual(TorqueSampleParser.MaxPidCount, sample.Values.Count, "PIDs beyond the bound are dropped, never rejected.");
            Assert.AreEqual(TorqueSampleParser.MaxEmailLength, sample.Email.Length);
            Assert.AreEqual(TorqueSampleParser.MaxProfileNameLength, sample.ProfileName.Length);
            Assert.AreEqual(TorqueSampleParser.MaxVersionLength, sample.Version.Length);
        }

        [TestMethod]
        public void Parser_Ignores_Unit_And_Profile_Metadata()
        {
            TorqueSample? sample = TorqueSampleParser.Parse(Query(
                ("session", "1"),
                ("time", "1764012346000"),
                ("userUnits", "1"),
                ("defaultUnits", "0"),
                ("profileFuel", "95"),
                ("profileName", "the van")));

            Assert.IsNotNull(sample);
            Assert.AreEqual(0, sample.Values.Count, "Only k* keys are PIDs.");
            Assert.AreEqual("the van", sample.ProfileName, "The profile name is the one useful metadata field.");
        }

        // ---- gate -----------------------------------------------------------------------

        private static FuelUsageRequest RequestFor(
            FuelUsageMonitorSettings settings,
            List<KeyValuePair<string, string>>? query = null,
            string? authorization = $"Bearer {Token}",
            string rawQuery = "?session=1&time=1764012346000",
            string remoteAddress = "10.0.0.1",
            bool maintenance = false)
        {
            return new FuelUsageRequest
            {
                Query = query ?? SampleQuery(),
                Authorization = authorization,
                RawQuery = rawQuery,
                RemoteAddress = remoteAddress,
                MaintenanceActive = maintenance
            };
        }

        [TestMethod]
        public void Gate_Accepts_An_Authenticated_Sample()
        {
            FuelUsageMonitorSettings settings = CreateSettings();

            FuelUsageIngestDecision decision = CreateGate(settings).Evaluate(RequestFor(settings));

            Assert.AreEqual(FuelUsageRequestGate.StatusOk, decision.StatusCode);
            Assert.AreEqual(FuelUsageOutcome.Sample, decision.Outcome);
            Assert.IsNotNull(decision.Sample);
        }

        [TestMethod]
        public void Gate_Acknowledges_A_Request_Without_A_Sample()
        {
            FuelUsageMonitorSettings settings = CreateSettings();

            FuelUsageIngestDecision decision = CreateGate(settings).Evaluate(
                RequestFor(settings, query: Query(), rawQuery: string.Empty));

            Assert.AreEqual(FuelUsageRequestGate.StatusOk, decision.StatusCode, "The Test settings ping must succeed.");
            Assert.AreEqual(FuelUsageOutcome.NotASample, decision.Outcome);
            Assert.IsNull(decision.Sample);
        }

        [TestMethod]
        public void Gate_Refuses_In_Maintenance_Before_Anything_Else()
        {
            FuelUsageMonitorSettings settings = CreateSettings();

            FuelUsageIngestDecision decision = CreateGate(settings).Evaluate(
                RequestFor(settings, authorization: null, maintenance: true));

            Assert.AreEqual(FuelUsageRequestGate.StatusServiceUnavailable, decision.StatusCode);
            Assert.AreEqual(FuelUsageOutcome.Maintenance, decision.Outcome);
        }

        [TestMethod]
        public void Gate_Rejects_A_Missing_Wrong_Or_Unexpected_Bearer()
        {
            FuelUsageMonitorSettings settings = CreateSettings();
            FuelUsageRequestGate gate = CreateGate(settings);

            Assert.AreEqual(FuelUsageRequestGate.StatusUnauthorized,
                gate.Evaluate(RequestFor(settings, authorization: null)).StatusCode);
            Assert.AreEqual(FuelUsageRequestGate.StatusUnauthorized,
                gate.Evaluate(RequestFor(settings, authorization: "Bearer wrong")).StatusCode);
            Assert.AreEqual(FuelUsageRequestGate.StatusUnauthorized,
                gate.Evaluate(RequestFor(settings, authorization: Token)).StatusCode);
        }

        [TestMethod]
        public void Gate_Rejects_An_Oversized_Query_String()
        {
            FuelUsageMonitorSettings settings = CreateSettings(maxQueryBytes: 128);

            FuelUsageIngestDecision decision = CreateGate(settings).Evaluate(
                RequestFor(settings, rawQuery: $"?session=1&pad={new string('x', 200)}"));

            Assert.AreEqual(FuelUsageRequestGate.StatusUriTooLong, decision.StatusCode);
            Assert.AreEqual(FuelUsageOutcome.QueryTooLarge, decision.Outcome);
        }

        [TestMethod]
        public void Gate_Rate_Limits_Per_Remote_Address()
        {
            FuelUsageMonitorSettings settings = CreateSettings(maxUploadsPerMinute: 2);
            FuelUsageRequestGate gate = CreateGate(settings);

            Assert.AreEqual(FuelUsageRequestGate.StatusOk, gate.Evaluate(RequestFor(settings)).StatusCode);
            Assert.AreEqual(FuelUsageRequestGate.StatusOk, gate.Evaluate(RequestFor(settings)).StatusCode);
            FuelUsageIngestDecision third = gate.Evaluate(RequestFor(settings));
            FuelUsageIngestDecision otherAddress = gate.Evaluate(RequestFor(settings, remoteAddress: "10.0.0.2"));

            Assert.AreEqual(FuelUsageRequestGate.StatusTooManyRequests, third.StatusCode);
            Assert.AreEqual(FuelUsageOutcome.RateLimited, third.Outcome);
            Assert.AreEqual(FuelUsageRequestGate.StatusOk, otherAddress.StatusCode, "Another sender keeps its own budget.");
        }

        [TestMethod]
        public void Rate_Limiter_Resets_On_The_Next_Minute()
        {
            var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
            var limiter = new FuelUsageRateLimiter(1, timeProvider);

            Assert.IsTrue(limiter.TryAcquire("10.0.0.1"));
            Assert.IsFalse(limiter.TryAcquire("10.0.0.1"));

            timeProvider.Advance(TimeSpan.FromSeconds(61));

            Assert.IsTrue(limiter.TryAcquire("10.0.0.1"), "The window rolls over with the minute.");
        }

        // ---- counters -------------------------------------------------------------------

        [TestMethod]
        public void Status_Reports_Counts_And_Keys_But_Never_Content()
        {
            var status = new FuelUsageIngestionStatus();
            status.SetState("listening at http://0.0.0.0:2526/fuel/upload");
            status.RecordAccepted(TorqueSampleParser.Parse(SampleQuery())!);
            status.RecordRefused(FuelUsageOutcome.Unauthorized);
            status.RecordRefused(FuelUsageOutcome.NotASample);
            status.RecordDropped();
            status.RecordBody(2048);

            string report = status.Describe();

            StringAssert.Contains(report, "listening at http://0.0.0.0:2526/fuel/upload");
            StringAssert.Contains(report, "accepted 1");
            StringAssert.Contains(report, "rejected 2");
            StringAssert.Contains(report, "dropped 1");
            StringAssert.Contains(report, "bodies 1 (2 KB)");
            StringAssert.Contains(report, "session 1764012345000");
            StringAssert.Contains(report, "kff1005");
            Assert.IsFalse(report.Contains("driver@example.com", StringComparison.Ordinal), "The sender's email never reaches the report.");
            Assert.IsFalse(report.Contains("50.08527848", StringComparison.Ordinal), "A GPS coordinate never reaches the report.");
        }

        [TestMethod]
        public void Status_Starts_Not_Started_And_Names_An_Empty_History()
        {
            string report = new FuelUsageIngestionStatus().Describe();

            StringAssert.Contains(report, "not started");
            StringAssert.Contains(report, "last never");
            StringAssert.Contains(report, "PIDs none");
        }

        // ---- endpoint and host ----------------------------------------------------------

        private sealed class Harness : IAsyncDisposable
        {
            private readonly FuelUsageWebHost _host;
            private readonly HttpClient _client = new();

            private Harness(
                FuelUsageWebHost host,
                FuelUsageMonitorSettings settings,
                Channel<TorqueSample> queue,
                FuelUsageIngestionStatus status,
                MaintenanceMode maintenance)
            {
                _host = host;
                Settings = settings;
                Queue = queue;
                Status = status;
                Maintenance = maintenance;
            }

            public FuelUsageMonitorSettings Settings { get; }
            public Channel<TorqueSample> Queue { get; }
            public FuelUsageIngestionStatus Status { get; }
            public MaintenanceMode Maintenance { get; }

            public static async Task<Harness> StartAsync(FuelUsageMonitorSettings settings)
            {
                var queue = Channel.CreateBounded<TorqueSample>(new BoundedChannelOptions(settings.QueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true
                });
                var status = new FuelUsageIngestionStatus();
                var maintenance = new MaintenanceMode();
                var endpoint = new FuelUsageEndpoint(
                    settings,
                    CreateGate(settings, new FakeTimeProvider()),
                    status,
                    queue.Writer,
                    maintenance,
                    new FakeTimeProvider(),
                    NullLogger.Instance);

                FuelUsageWebHost host = await FuelUsageWebHost.StartAsync(settings, endpoint.HandleAsync, CancellationToken.None);
                return new Harness(host, settings, queue, status, maintenance);
            }

            private Uri RequestUrl(string query) => new(_host.BaseUri, $"{Settings.ListenPath.TrimStart('/')}{query}");

            public async Task<HttpResponseMessage> GetAsync(string query = "", string? bearer = Token)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, RequestUrl(query));
                AddBearer(request, bearer);
                return await _client.SendAsync(request);
            }

            public async Task<HttpResponseMessage> PostAsync(HttpContent? content = null, string query = "", string? bearer = Token)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, RequestUrl(query))
                {
                    Content = content
                };
                AddBearer(request, bearer);
                return await _client.SendAsync(request);
            }

            public async Task<HttpResponseMessage> GetPathAsync(string relativePath)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_host.BaseUri, relativePath.TrimStart('/')));
                AddBearer(request, Token);
                return await _client.SendAsync(request);
            }

            private static void AddBearer(HttpRequestMessage request, string? bearer)
            {
                if (bearer is not null)
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
            }

            public async ValueTask DisposeAsync()
            {
                _client.Dispose();
                await _host.DisposeAsync();
            }
        }

        [TestMethod]
        public async Task Endpoint_Accepts_A_Sample_On_Both_Methods_And_Answers_Ok()
        {
            await using Harness harness = await Harness.StartAsync(CreateSettings());

            using HttpResponseMessage get = await harness.GetAsync(SampleQueryString);
            using HttpResponseMessage post = await harness.PostAsync(query: SampleQueryString);

            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, post.StatusCode);
            Assert.AreEqual("OK!", await get.Content.ReadAsStringAsync(), "The app checks this exact body.");
            Assert.AreEqual("OK!", await post.Content.ReadAsStringAsync());

            Assert.IsTrue(harness.Queue.Reader.TryRead(out TorqueSample? first));
            Assert.IsTrue(harness.Queue.Reader.TryRead(out TorqueSample? second));
            Assert.AreEqual(87.5, first!.Values["kd"]);
            Assert.AreEqual(87.5, second!.Values["kd"]);
        }

        [TestMethod]
        public async Task Endpoint_Acknowledges_A_Test_Settings_Ping_Without_Queuing_Anything()
        {
            await using Harness harness = await Harness.StartAsync(CreateSettings());

            using HttpResponseMessage response = await harness.GetAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("OK!", await response.Content.ReadAsStringAsync());
            Assert.IsFalse(harness.Queue.Reader.TryRead(out _), "Nothing is a sample without session/id + time.");
            StringAssert.Contains(harness.Status.Describe(), "rejected 1");
        }

        [TestMethod]
        public async Task Endpoint_Accepts_And_Discards_An_Uploaded_File()
        {
            await using Harness harness = await Harness.StartAsync(CreateSettings());
            string csv = "GPS Time,Device Time,Speed (OBD)(km/h)"
                + Environment.NewLine + "1764012346,1764012346,87.5" + Environment.NewLine;
            using var content = new MultipartFormDataContent
            {
                { new StringContent(csv, Encoding.UTF8, "text/csv"), "file", "session.csv" }
            };

            using HttpResponseMessage response = await harness.PostAsync(content);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("OK!", await response.Content.ReadAsStringAsync(), "An upload is acknowledged so the transport works.");
            Assert.IsFalse(harness.Queue.Reader.TryRead(out _), "The body is never parsed into a sample.");
            string status = harness.Status.Describe();
            StringAssert.Contains(status, "bodies 1", "The discarded body is counted.");
            StringAssert.Contains(status, "rejected 1", "It is acknowledged as not-a-sample, not as a trip.");
        }

        [TestMethod]
        public async Task Endpoint_Keeps_The_Sample_When_A_Body_Rides_Along()
        {
            await using Harness harness = await Harness.StartAsync(CreateSettings());
            using var content = new StringContent("irrelevant", Encoding.UTF8, "text/plain");

            using HttpResponseMessage response = await harness.PostAsync(content, SampleQueryString);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(harness.Queue.Reader.TryRead(out TorqueSample? sample), "The query string decides, the body changes nothing.");
            Assert.AreEqual("1764012345000", sample!.Session);
        }

        [TestMethod]
        public async Task Endpoint_Refuses_An_Oversized_Body()
        {
            await using Harness harness = await Harness.StartAsync(CreateSettings(maxBodyBytes: 1024));
            using var content = new ByteArrayContent(new byte[8 * 1024]);

            using HttpResponseMessage response = await harness.PostAsync(content);

            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            StringAssert.Contains(harness.Status.Describe(), "rejected 1");
        }

        [TestMethod]
        public async Task Endpoint_Refuses_A_Request_Without_The_Token()
        {
            await using Harness harness = await Harness.StartAsync(CreateSettings());

            using HttpResponseMessage response = await harness.GetAsync(SampleQueryString, bearer: null);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.IsFalse(harness.Queue.Reader.TryRead(out _));
        }

        [TestMethod]
        public async Task Endpoint_Does_Not_Serve_Other_Paths()
        {
            await using Harness harness = await Harness.StartAsync(CreateSettings());

            using HttpResponseMessage other = await harness.GetPathAsync("somewhere-else");
            using HttpResponseMessage deeper = await harness.GetPathAsync("fuel/upload/somewhere-else");

            Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, deeper.StatusCode, "Only the configured route is mapped.");
        }

        [TestMethod]
        public async Task Endpoint_Answers_Maintenance_With_Service_Unavailable()
        {
            await using Harness harness = await Harness.StartAsync(CreateSettings());
            harness.Maintenance.Enter();

            using HttpResponseMessage response = await harness.GetAsync(SampleQueryString);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.IsFalse(harness.Queue.Reader.TryRead(out _), "Nothing is queued for replay.");
        }

        [TestMethod]
        public async Task Endpoint_Drops_A_Sample_When_The_Queue_Is_Full_And_Still_Acknowledges()
        {
            await using Harness harness = await Harness.StartAsync(CreateSettings(queueCapacity: 1));

            using HttpResponseMessage first = await harness.GetAsync(SampleQueryString);
            using HttpResponseMessage second = await harness.GetAsync(SampleQueryString);

            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, "A drop never becomes a failed upload.");
            Assert.AreEqual("OK!", await second.Content.ReadAsStringAsync());
            StringAssert.Contains(harness.Status.Describe(), "dropped 1");
        }
    }
}
