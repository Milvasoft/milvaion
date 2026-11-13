{{/* vim: set filetype=mustache: */}}

{{- define "milvaion.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "milvaion.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "milvaion.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "milvaion.labels" -}}
helm.sh/chart: {{ include "milvaion.chart" . }}
{{ include "milvaion.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: milvaion
{{- with .Values.commonLabels }}
{{ toYaml . }}
{{- end }}
{{- end -}}

{{- define "milvaion.selectorLabels" -}}
app.kubernetes.io/name: {{ include "milvaion.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "milvaion.api.fullname" -}}
{{- printf "%s-api" (include "milvaion.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "milvaion.api.selectorLabels" -}}
{{ include "milvaion.selectorLabels" . }}
app.kubernetes.io/component: api
{{- end -}}

{{- define "milvaion.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "milvaion.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/*
Image reference. Call with (dict "image" .Values.api.image "root" $ "defaultTag" "...").
*/}}
{{- define "milvaion.image" -}}
{{- $repo := .image.repository -}}
{{- with .root.Values.global.imageRegistry -}}
{{- $repo = printf "%s/%s" (trimSuffix "/" .) $repo -}}
{{- end -}}
{{- if .image.digest -}}
{{- printf "%s@%s" $repo .image.digest -}}
{{- else -}}
{{- printf "%s:%s" $repo (toString (default .defaultTag .image.tag)) -}}
{{- end -}}
{{- end -}}

{{/* ---------------------------------------------------------------------------------------------------------
     Secrets
     --------------------------------------------------------------------------------------------------------- */}}

{{/* Name of the Secret the chart creates. */}}
{{- define "milvaion.secretName" -}}
{{- include "milvaion.fullname" . -}}
{{- end -}}

{{/* Secret holding the JWT key, API key secret and root password. */}}
{{- define "milvaion.auth.secretName" -}}
{{- default (include "milvaion.secretName" .) .Values.auth.existingSecret -}}
{{- end -}}

{{/*
A value for a key of the chart's Secret: the explicit value, else the one already stored in the cluster (so
generated values survive upgrades), else a new random string. Call with (dict "root" $ "key" "..." "value" "...").
*/}}
{{- define "milvaion.secretValue" -}}
{{- if .value -}}
{{- .value -}}
{{- else -}}
{{- $existing := lookup "v1" "Secret" .root.Release.Namespace (include "milvaion.secretName" .root) -}}
{{- if and $existing $existing.data (hasKey $existing.data .key) -}}
{{- index $existing.data .key | b64dec -}}
{{- else -}}
{{- randAlphaNum (default 48 .length | int) -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* Fails with every missing connection setting at once, instead of the first one a template happens to hit. */}}
{{- define "milvaion.validate" -}}
{{- $missing := list -}}
{{- if and (not .Values.postgresql.enabled) (not .Values.externalDatabase.host) -}}
{{- $missing = append $missing "externalDatabase.host (or postgresql.enabled=true)" -}}
{{- end -}}
{{- if and (not .Values.postgresql.enabled) (not .Values.externalDatabase.existingSecret) (not .Values.externalDatabase.password) -}}
{{- $missing = append $missing "externalDatabase.existingSecret or externalDatabase.password" -}}
{{- end -}}
{{- if and (not .Values.rabbitmq.enabled) (not .Values.externalRabbitmq.host) -}}
{{- $missing = append $missing "externalRabbitmq.host (or rabbitmq.enabled=true)" -}}
{{- end -}}
{{- if and (not .Values.rabbitmq.enabled) (not .Values.externalRabbitmq.existingSecret) (not .Values.externalRabbitmq.password) -}}
{{- $missing = append $missing "externalRabbitmq.existingSecret or externalRabbitmq.password" -}}
{{- end -}}
{{- if and (not .Values.redis.enabled) (not .Values.externalRedis.host) -}}
{{- $missing = append $missing "externalRedis.host (or redis.enabled=true)" -}}
{{- end -}}
{{- if $missing -}}
{{- fail (printf "\n\nMilvaion needs PostgreSQL, RabbitMQ and Redis. Set:\n  - %s\n\nFor a quick evaluation install, enable the bundled ones: --set postgresql.enabled=true,rabbitmq.enabled=true,redis.enabled=true\nThey are single pods without backups; see the chart README before using them in production.\n" (join "\n  - " $missing)) -}}
{{- end -}}
{{- end -}}

{{/* ---------------------------------------------------------------------------------------------------------
     PostgreSQL
     --------------------------------------------------------------------------------------------------------- */}}

{{- define "milvaion.database.host" -}}
{{- if .Values.postgresql.enabled -}}
{{- printf "%s-postgresql" .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- required "Set externalDatabase.host, or postgresql.enabled=true for an evaluation install." .Values.externalDatabase.host -}}
{{- end -}}
{{- end -}}

{{- define "milvaion.database.port" -}}
{{- if .Values.postgresql.enabled -}}5432{{- else -}}{{- .Values.externalDatabase.port -}}{{- end -}}
{{- end -}}

{{- define "milvaion.database.name" -}}
{{- if .Values.postgresql.enabled -}}{{- .Values.postgresql.auth.database -}}{{- else -}}{{- .Values.externalDatabase.database -}}{{- end -}}
{{- end -}}

{{- define "milvaion.database.username" -}}
{{- if .Values.postgresql.enabled -}}{{- .Values.postgresql.auth.username -}}{{- else -}}{{- .Values.externalDatabase.username -}}{{- end -}}
{{- end -}}

{{- define "milvaion.database.secretName" -}}
{{- if .Values.postgresql.enabled -}}
{{- printf "%s-postgresql" .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else if .Values.externalDatabase.existingSecret -}}
{{- .Values.externalDatabase.existingSecret -}}
{{- else -}}
{{- include "milvaion.secretName" . -}}
{{- end -}}
{{- end -}}

{{- define "milvaion.database.secretKey" -}}
{{- if .Values.postgresql.enabled -}}password{{- else -}}{{- .Values.externalDatabase.existingSecretPasswordKey -}}{{- end -}}
{{- end -}}

{{/*
Connection string with the password taken from $(MILVAION_DB_PASSWORD), which must be declared first.
Call with (dict "root" $ "parameters" "Npgsql settings to append").
*/}}
{{- define "milvaion.database.connectionString" -}}
{{- $root := .root -}}
{{- $s := printf "Host=%s;Port=%s;Database=%s;Username=%s;Password=$(MILVAION_DB_PASSWORD)" (include "milvaion.database.host" $root) (include "milvaion.database.port" $root) (include "milvaion.database.name" $root) (include "milvaion.database.username" $root) -}}
{{- with .parameters -}}
{{- $s = printf "%s;%s" $s (trimPrefix ";" .) -}}
{{- end -}}
{{- $s -}}
{{- end -}}

{{/* ---------------------------------------------------------------------------------------------------------
     RabbitMQ
     --------------------------------------------------------------------------------------------------------- */}}

{{- define "milvaion.rabbitmq.host" -}}
{{- if .Values.rabbitmq.enabled -}}
{{- printf "%s-rabbitmq" .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- required "Set externalRabbitmq.host, or rabbitmq.enabled=true for an evaluation install." .Values.externalRabbitmq.host -}}
{{- end -}}
{{- end -}}

{{- define "milvaion.rabbitmq.port" -}}
{{- if .Values.rabbitmq.enabled -}}5672{{- else -}}{{- .Values.externalRabbitmq.port -}}{{- end -}}
{{- end -}}

{{- define "milvaion.rabbitmq.username" -}}
{{- if .Values.rabbitmq.enabled -}}{{- .Values.rabbitmq.auth.username -}}{{- else -}}{{- .Values.externalRabbitmq.username -}}{{- end -}}
{{- end -}}

{{- define "milvaion.rabbitmq.virtualHost" -}}
{{- if .Values.rabbitmq.enabled -}}/{{- else -}}{{- .Values.externalRabbitmq.virtualHost -}}{{- end -}}
{{- end -}}

{{- define "milvaion.rabbitmq.secretName" -}}
{{- if .Values.rabbitmq.enabled -}}
{{- printf "%s-rabbitmq" .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else if .Values.externalRabbitmq.existingSecret -}}
{{- .Values.externalRabbitmq.existingSecret -}}
{{- else -}}
{{- include "milvaion.secretName" . -}}
{{- end -}}
{{- end -}}

{{- define "milvaion.rabbitmq.secretKey" -}}
{{- if .Values.rabbitmq.enabled -}}password{{- else -}}{{- .Values.externalRabbitmq.existingSecretPasswordKey -}}{{- end -}}
{{- end -}}

{{/* ---------------------------------------------------------------------------------------------------------
     Redis
     --------------------------------------------------------------------------------------------------------- */}}

{{- define "milvaion.redis.host" -}}
{{- if .Values.redis.enabled -}}
{{- printf "%s-redis" .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- required "Set externalRedis.host, or redis.enabled=true for an evaluation install." .Values.externalRedis.host -}}
{{- end -}}
{{- end -}}

{{- define "milvaion.redis.port" -}}
{{- if .Values.redis.enabled -}}6379{{- else -}}{{- .Values.externalRedis.port -}}{{- end -}}
{{- end -}}

{{- define "milvaion.redis.database" -}}
{{- if .Values.redis.enabled -}}0{{- else -}}{{- .Values.externalRedis.database -}}{{- end -}}
{{- end -}}

{{/* "true" when Redis needs a password. */}}
{{- define "milvaion.redis.authEnabled" -}}
{{- if .Values.redis.enabled -}}
{{- if .Values.redis.auth.enabled -}}true{{- end -}}
{{- else if or .Values.externalRedis.password .Values.externalRedis.existingSecret -}}true{{- end -}}
{{- end -}}

{{- define "milvaion.redis.secretName" -}}
{{- if .Values.redis.enabled -}}
{{- printf "%s-redis" .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else if .Values.externalRedis.existingSecret -}}
{{- .Values.externalRedis.existingSecret -}}
{{- else -}}
{{- include "milvaion.secretName" . -}}
{{- end -}}
{{- end -}}

{{- define "milvaion.redis.secretKey" -}}
{{- if .Values.redis.enabled -}}password{{- else -}}{{- .Values.externalRedis.existingSecretPasswordKey -}}{{- end -}}
{{- end -}}

{{/* ---------------------------------------------------------------------------------------------------------
     Shared environment
     --------------------------------------------------------------------------------------------------------- */}}

{{/* Password variables that other variables expand with $(NAME). They must come first in the env list. */}}
{{- define "milvaion.env.passwords" -}}
- name: MILVAION_RABBITMQ_PASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ include "milvaion.rabbitmq.secretName" . }}
      key: {{ include "milvaion.rabbitmq.secretKey" . }}
{{- if include "milvaion.redis.authEnabled" . }}
- name: MILVAION_REDIS_PASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ include "milvaion.redis.secretName" . }}
      key: {{ include "milvaion.redis.secretKey" . }}
{{- end }}
{{- end -}}

{{- define "milvaion.env.dbPassword" -}}
- name: MILVAION_DB_PASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ include "milvaion.database.secretName" . }}
      key: {{ include "milvaion.database.secretKey" . }}
{{- end -}}

{{/* Redis connection string for StackExchange.Redis, with the password inline when there is one. */}}
{{- define "milvaion.redis.connectionString" -}}
{{- $s := printf "%s:%s" (include "milvaion.redis.host" .) (include "milvaion.redis.port" .) -}}
{{- if include "milvaion.redis.authEnabled" . -}}
{{- $s = printf "%s,password=$(MILVAION_REDIS_PASSWORD)" $s -}}
{{- end -}}
{{- $s -}}
{{- end -}}

{{/*
Flattens a nested map into ASP.NET Core configuration keys joined with "__".
Call with (dict "prefix" "" "value" $map "out" $dict); the result is written into $dict.
*/}}
{{- define "milvaion.flatten" -}}
{{- $prefix := .prefix -}}
{{- $out := .out -}}
{{- if kindIs "map" .value -}}
{{- range $k, $v := .value -}}
{{- $key := ternary $k (printf "%s__%s" $prefix $k) (eq $prefix "") -}}
{{- include "milvaion.flatten" (dict "prefix" $key "value" $v "out" $out) -}}
{{- end -}}
{{- else if kindIs "slice" .value -}}
{{- range $i, $v := .value -}}
{{- include "milvaion.flatten" (dict "prefix" (printf "%s__%d" $prefix $i) "value" $v "out" $out) -}}
{{- end -}}
{{- else if kindIs "float64" .value -}}
{{- if eq .value (float64 (int64 .value)) -}}
{{- $_ := set $out $prefix (toString (int64 .value)) -}}
{{- else -}}
{{- $_ := set $out $prefix (toString .value) -}}
{{- end -}}
{{- else if kindIs "invalid" .value -}}
{{- $_ := set $out $prefix "" -}}
{{- else -}}
{{- $_ := set $out $prefix (toString .value) -}}
{{- end -}}
{{- end -}}

{{/*
Deep merge where every value in src wins, including false, 0 and "" (sprig's merge functions skip those).
Maps are merged key by key; lists and scalars are replaced. Call with (dict "dst" $map "src" $map).
*/}}
{{- define "milvaion.deepMerge" -}}
{{- $dst := .dst -}}
{{- range $k, $v := .src -}}
{{- if and (kindIs "map" $v) (hasKey $dst $k) (kindIs "map" (index $dst $k)) -}}
{{- include "milvaion.deepMerge" (dict "dst" (index $dst $k) "src" $v) -}}
{{- else -}}
{{- $_ := set $dst $k $v -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* Merged settings for one worker, as YAML: workerDefaults overlaid with the worker's own values. */}}
{{- define "milvaion.worker.values" -}}
{{- $merged := deepCopy .root.Values.workerDefaults -}}
{{- include "milvaion.deepMerge" (dict "dst" $merged "src" (deepCopy .worker)) -}}
{{- toYaml $merged -}}
{{- end -}}
