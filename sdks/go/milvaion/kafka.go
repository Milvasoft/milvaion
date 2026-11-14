package milvaion

import (
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"os"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode"

	"github.com/twmb/franz-go/pkg/kgo"
	"github.com/twmb/franz-go/pkg/sasl"
	"github.com/twmb/franz-go/pkg/sasl/plain"
	"github.com/twmb/franz-go/pkg/sasl/scram"
)

// Kafka wire contract shared with the Milvaion API and the other SDKs.
// See docs/githubdocs/KAFKA-TRANSPORT.md in the repository.

const (
	transportRabbitMQ = "rabbitmq"
	transportKafka    = "kafka"

	headerOccurrenceID  = "OccurrenceId"
	headerCorrelationID = "CorrelationId"
	headerRetryCount    = "x-retry-count"
	headerMaxRetries    = "MaxRetries"
	headerNotBefore     = "x-not-before"
	headerOriginalTopic = "x-original-topic"
	headerDLQReason     = "x-dlq-reason"
)

// KafkaOptions configures the Kafka transport. The zero value connects to localhost:9092 without
// TLS or SASL and uses the defaults below.
type KafkaOptions struct {
	// Brokers is the bootstrap list ("host:port"); an entry may itself be comma separated.
	// Default: localhost:9092. Env: MILVAION_KAFKA_BOOTSTRAP_SERVERS or KAFKA_BOOTSTRAP_SERVERS.
	Brokers []string

	// TLS enables TLS. Setting any of the files below enables it too. Env: MILVAION_KAFKA_TLS=true.
	TLS bool
	// TLSCAFile is a PEM bundle used to verify the brokers. Default: the system roots.
	// Env: MILVAION_KAFKA_TLS_CA_FILE.
	TLSCAFile string
	// TLSCertFile and TLSKeyFile are the PEM client certificate and key for mutual TLS.
	// Env: MILVAION_KAFKA_TLS_CERT_FILE, MILVAION_KAFKA_TLS_KEY_FILE.
	TLSCertFile string
	TLSKeyFile  string

	// SASLMechanism is "plain", "scram-sha-256" or "scram-sha-512". Empty disables SASL.
	// Env: MILVAION_KAFKA_SASL_MECHANISM, MILVAION_KAFKA_SASL_USERNAME, MILVAION_KAFKA_SASL_PASSWORD.
	SASLMechanism string
	SASLUsername  string
	SASLPassword  string

	// TopicPrefix is prepended to every topic and consumer group name, for example "prod.". It must
	// match the API's. Only a-z A-Z 0-9 . _ - are allowed. Env: MILVAION_KAFKA_TOPIC_PREFIX.
	TopicPrefix string

	// DisableAutoCreateTopics stops the worker from creating its topics at startup.
	// Env: MILVAION_KAFKA_AUTO_CREATE_TOPICS=false.
	DisableAutoCreateTopics bool
	// NumPartitions of auto-created topics. Default: 12. Env: MILVAION_KAFKA_PARTITIONS.
	NumPartitions int
	// ReplicationFactor of auto-created topics. Default: -1, the broker's default.replication.factor.
	// Env: MILVAION_KAFKA_REPLICATION_FACTOR.
	ReplicationFactor int
	// MaxInFlightPerPartition is how many jobs of one partition may run at the same time.
	// Default: the worker's MaxParallelJobs. Env: MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION.
	MaxInFlightPerPartition int
}

type resolvedKafka struct {
	brokers                 []string
	tls                     *tls.Config
	sasl                    sasl.Mechanism
	topicPrefix             string
	autoCreateTopics        bool
	numPartitions           int32
	replicationFactor       int16
	maxInFlightPerPartition int
}

var topicPrefixPattern = regexp.MustCompile(`^[A-Za-z0-9._-]*$`)

func resolveKafka(o KafkaOptions, maxParallelJobs int) (resolvedKafka, error) {
	var brokers []string

	for _, entry := range o.Brokers {
		for _, broker := range strings.Split(entry, ",") {
			if broker = strings.TrimSpace(broker); broker != "" {
				brokers = append(brokers, broker)
			}
		}
	}

	if len(brokers) == 0 {
		brokers = []string{"localhost:9092"}
	}

	if !topicPrefixPattern.MatchString(o.TopicPrefix) {
		return resolvedKafka{}, fmt.Errorf("milvaion: Kafka.TopicPrefix may only contain a-z A-Z 0-9 . _ -, got %q", o.TopicPrefix)
	}

	partitions := o.NumPartitions

	if partitions == 0 {
		partitions = 12
	}

	if partitions < 1 || partitions > 1<<20 {
		return resolvedKafka{}, fmt.Errorf("milvaion: Kafka.NumPartitions must be positive, got %d", o.NumPartitions)
	}

	replication := o.ReplicationFactor

	if replication == 0 {
		replication = -1
	}

	if replication < -1 || replication > 32767 {
		return resolvedKafka{}, fmt.Errorf("milvaion: Kafka.ReplicationFactor must be -1 or positive, got %d", o.ReplicationFactor)
	}

	inFlight := o.MaxInFlightPerPartition

	if inFlight == 0 {
		inFlight = maxParallelJobs
	}

	if inFlight < 1 {
		return resolvedKafka{}, fmt.Errorf("milvaion: Kafka.MaxInFlightPerPartition must be positive, got %d", o.MaxInFlightPerPartition)
	}

	tlsConfig, err := kafkaTLS(o)

	if err != nil {
		return resolvedKafka{}, err
	}

	mechanism, err := kafkaSASL(o)

	if err != nil {
		return resolvedKafka{}, err
	}

	return resolvedKafka{
		brokers:                 brokers,
		tls:                     tlsConfig,
		sasl:                    mechanism,
		topicPrefix:             o.TopicPrefix,
		autoCreateTopics:        !o.DisableAutoCreateTopics,
		numPartitions:           int32(partitions),
		replicationFactor:       int16(replication),
		maxInFlightPerPartition: inFlight,
	}, nil
}

func kafkaTLS(o KafkaOptions) (*tls.Config, error) {
	if !o.TLS && o.TLSCAFile == "" && o.TLSCertFile == "" && o.TLSKeyFile == "" {
		return nil, nil
	}

	config := &tls.Config{MinVersion: tls.VersionTLS12}

	if o.TLSCAFile != "" {
		pem, err := os.ReadFile(o.TLSCAFile)

		if err != nil {
			return nil, fmt.Errorf("milvaion: reading Kafka.TLSCAFile: %w", err)
		}

		pool := x509.NewCertPool()

		if !pool.AppendCertsFromPEM(pem) {
			return nil, fmt.Errorf("milvaion: Kafka.TLSCAFile %q contains no PEM certificate", o.TLSCAFile)
		}

		config.RootCAs = pool
	}

	if (o.TLSCertFile == "") != (o.TLSKeyFile == "") {
		return nil, errors.New("milvaion: Kafka.TLSCertFile and Kafka.TLSKeyFile must be set together")
	}

	if o.TLSCertFile != "" {
		cert, err := tls.LoadX509KeyPair(o.TLSCertFile, o.TLSKeyFile)

		if err != nil {
			return nil, fmt.Errorf("milvaion: loading the Kafka client certificate: %w", err)
		}

		config.Certificates = []tls.Certificate{cert}
	}

	return config, nil
}

func kafkaSASL(o KafkaOptions) (sasl.Mechanism, error) {
	mechanism := strings.NewReplacer("-", "", "_", "").Replace(strings.ToLower(strings.TrimSpace(o.SASLMechanism)))

	if mechanism == "" {
		return nil, nil
	}

	if o.SASLUsername == "" {
		return nil, errors.New("milvaion: Kafka.SASLUsername is required with Kafka.SASLMechanism")
	}

	switch mechanism {
	case "plain":
		return plain.Auth{User: o.SASLUsername, Pass: o.SASLPassword}.AsMechanism(), nil
	case "scramsha256":
		return scram.Auth{User: o.SASLUsername, Pass: o.SASLPassword}.AsSha256Mechanism(), nil
	case "scramsha512":
		return scram.Auth{User: o.SASLUsername, Pass: o.SASLPassword}.AsSha512Mechanism(), nil
	default:
		return nil, fmt.Errorf("milvaion: Kafka.SASLMechanism must be \"plain\", \"scram-sha-256\" or \"scram-sha-512\", got %q", o.SASLMechanism)
	}
}

// kafkaEnv fills the Kafka options that are not set from MILVAION_KAFKA_* variables. The integer
// settings are read by OptionsFromEnv with the other integers.
func kafkaEnv(k *KafkaOptions, env func(string) string) {
	if len(k.Brokers) == 0 {
		if brokers := stringOr(env("MILVAION_KAFKA_BOOTSTRAP_SERVERS"), env("KAFKA_BOOTSTRAP_SERVERS")); brokers != "" {
			k.Brokers = []string{brokers}
		}
	}

	if !k.TLS {
		k.TLS = strings.EqualFold(env("MILVAION_KAFKA_TLS"), "true")
	}

	strs := []struct {
		field *string
		key   string
	}{
		{&k.TLSCAFile, "MILVAION_KAFKA_TLS_CA_FILE"},
		{&k.TLSCertFile, "MILVAION_KAFKA_TLS_CERT_FILE"},
		{&k.TLSKeyFile, "MILVAION_KAFKA_TLS_KEY_FILE"},
		{&k.SASLMechanism, "MILVAION_KAFKA_SASL_MECHANISM"},
		{&k.SASLUsername, "MILVAION_KAFKA_SASL_USERNAME"},
		{&k.SASLPassword, "MILVAION_KAFKA_SASL_PASSWORD"},
		{&k.TopicPrefix, "MILVAION_KAFKA_TOPIC_PREFIX"},
	}

	for _, s := range strs {
		if *s.field == "" {
			*s.field = env(s.key)
		}
	}

	if !k.DisableAutoCreateTopics {
		k.DisableAutoCreateTopics = strings.EqualFold(env("MILVAION_KAFKA_AUTO_CREATE_TOPICS"), "false")
	}
}

// kafkaTopics are the topic and group names of one worker.
type kafkaTopics struct {
	jobs, retry, status, logs, heartbeats, registrations, dlq, group string
}

func newKafkaTopics(prefix, workerID string) kafkaTopics {
	worker := kafkaWorkerSegment(workerID)

	return kafkaTopics{
		jobs:          prefix + "milvaion.jobs." + worker,
		retry:         prefix + "milvaion.jobs." + worker + ".retry",
		status:        prefix + "milvaion.status",
		logs:          prefix + "milvaion.logs",
		heartbeats:    prefix + "milvaion.heartbeats",
		registrations: prefix + "milvaion.registrations",
		dlq:           prefix + "milvaion.dlq",
		group:         prefix + "milvaion.worker." + worker,
	}
}

// kafkaWorkerSegment is the worker id as it appears in topic and group names: lower case, with
// every character outside a-z 0-9 . _ - replaced by "_".
func kafkaWorkerSegment(workerID string) string {
	var b strings.Builder

	for _, r := range workerID {
		r = unicode.ToLower(r)

		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '.' || r == '_' || r == '-' {
			b.WriteRune(r)
		} else {
			b.WriteByte('_')
		}
	}

	return b.String()
}

// kafkaHeaderMap turns record headers into the map the processor reads. Values stay strings; the
// last header of a name wins.
func kafkaHeaderMap(headers []kgo.RecordHeader) map[string]any {
	out := make(map[string]any, len(headers))

	for _, h := range headers {
		out[h.Key] = string(h.Value)
	}

	return out
}

// kafkaRecordHeaders encodes headers as UTF-8 strings, numbers as decimal strings, in a stable order.
func kafkaRecordHeaders(headers map[string]any) []kgo.RecordHeader {
	keys := make([]string, 0, len(headers))

	for k := range headers {
		keys = append(keys, k)
	}

	sort.Strings(keys)
	out := make([]kgo.RecordHeader, 0, len(keys))

	for _, k := range keys {
		out = append(out, kgo.RecordHeader{Key: k, Value: []byte(kafkaHeaderValue(headers[k]))})
	}

	return out
}

func kafkaHeaderValue(value any) string {
	switch v := value.(type) {
	case string:
		return v
	case []byte:
		return string(v)
	case int:
		return strconv.Itoa(v)
	case int8:
		return strconv.FormatInt(int64(v), 10)
	case int16:
		return strconv.FormatInt(int64(v), 10)
	case int32:
		return strconv.FormatInt(int64(v), 10)
	case int64:
		return strconv.FormatInt(v, 10)
	case uint8:
		return strconv.FormatUint(uint64(v), 10)
	case uint16:
		return strconv.FormatUint(uint64(v), 10)
	case uint32:
		return strconv.FormatUint(uint64(v), 10)
	case uint64:
		return strconv.FormatUint(v, 10)
	case nil:
		return ""
	default:
		return fmt.Sprint(v)
	}
}

// kafkaNotBefore reads x-not-before (Unix milliseconds). ok is false when the header is missing or invalid.
func kafkaNotBefore(headers map[string]any) (time.Time, bool) {
	ms, err := strconv.ParseInt(strings.TrimSpace(headerString(headers[headerNotBefore])), 10, 64)

	if err != nil {
		return time.Time{}, false
	}

	return time.UnixMilli(ms), true
}

// offsetTracker follows the records of one partition that this instance has taken, in offset
// order, and knows how far the partition may be committed: up to the first record that is not
// finished, because a Kafka commit covers every record before it.
type offsetTracker struct {
	pending  []trackedOffset
	last     int64
	position int64
}

type trackedOffset struct {
	offset int64
	done   bool
}

func newOffsetTracker() *offsetTracker {
	return &offsetTracker{last: -1, position: -1}
}

// add registers a record taken from the partition. Offsets must increase; a repeated or older
// offset returns false and is ignored.
func (t *offsetTracker) add(offset int64) bool {
	if offset <= t.last {
		return false
	}

	t.last = offset
	t.pending = append(t.pending, trackedOffset{offset: offset})

	return true
}

// done marks a record finished. It returns false for an offset the tracker does not hold.
func (t *offsetTracker) done(offset int64) bool {
	i := sort.Search(len(t.pending), func(i int) bool { return t.pending[i].offset >= offset })

	if i == len(t.pending) || t.pending[i].offset != offset {
		return false
	}

	t.pending[i].done = true

	for len(t.pending) > 0 && t.pending[0].done {
		t.position = t.pending[0].offset + 1
		t.pending = t.pending[1:]
	}

	if len(t.pending) == 0 {
		t.pending = nil
	}

	return true
}

// commitPosition is the offset to commit: one past the end of the contiguous finished range.
// ok is false until the first record has finished.
func (t *offsetTracker) commitPosition() (int64, bool) {
	return t.position, t.position >= 0
}

// outstanding is the number of taken records that are not committed-ready yet.
func (t *offsetTracker) outstanding() int {
	return len(t.pending)
}
