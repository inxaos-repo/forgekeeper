{{/*
Expand the name of the chart.
*/}}
{{- define "forgekeeper.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "forgekeeper.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "forgekeeper.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels.
*/}}
{{- define "forgekeeper.labels" -}}
helm.sh/chart: {{ include "forgekeeper.chart" . }}
{{ include "forgekeeper.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels.
*/}}
{{- define "forgekeeper.selectorLabels" -}}
app.kubernetes.io/name: {{ include "forgekeeper.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
CNPG cluster name.
*/}}
{{- define "forgekeeper.pgcluster" -}}
{{- printf "%s-pgsql" (include "forgekeeper.fullname" .) }}
{{- end }}

{{/*
CNPG app secret name (created automatically by CNPG operator).
*/}}
{{- define "forgekeeper.pgSecretName" -}}
{{- printf "%s-app" (include "forgekeeper.pgcluster" .) }}
{{- end }}
