---
id: time-zones
title: "Time Zones"
sidebar_label: "Time Zones"
sidebar_position: 57
description: "Run a job at a local wall clock time in any IANA time zone, with predictable behaviour when daylight saving time starts and ends."
keywords: [milvaion time zone, cron time zone, daylight saving time, dst, iana time zone]
---

# Time Zones

A job's cron expression is read as wall clock time in the job's time zone. A job with the expression `0 0 9 * * MON-FRI` and the zone `Europe/Istanbul` runs at 09:00 Istanbul time on weekdays, all year round.

The default zone is `UTC`, which is how every job was evaluated before this setting existed. Existing jobs keep running exactly as they did.

## Setting the zone

- **Dashboard:** the job form has a searchable time zone picker under the cron expression. Type a city, a region or an offset such as `+03`.
- **API:** send `timeZoneId` with `POST /api/v1/jobs/job` or, as an update property, with `PUT /api/v1/jobs/job`.
- **MCP:** pass `timeZoneId` to `create_job` or `update_job`.

```json
{
  "displayName": "Morning report",
  "selectedJobName": "ReportJob",
  "cronExpression": "0 0 9 * * *",
  "timeZoneId": "Europe/Istanbul"
}
```

The id must be an IANA time zone id such as `UTC`, `Europe/London`, `America/New_York` or `Asia/Tokyo`. Windows ids such as `Turkey Standard Time` are rejected, so each zone has one spelling.

When you change the zone of an existing job, its next run moves to the same wall clock time in the new zone.

## Daylight saving time

Twice a year, in zones that observe daylight saving time, the wall clock jumps. Milvaion follows the wall clock:

| Transition | Example | What happens |
|------------|---------|--------------|
| Clocks go forward | In Berlin on 29 March 2026, 02:00 becomes 03:00. 02:30 never happens. | A run at a time inside the gap **does not run that day**. Runs at other times are not affected. |
| Clocks go back | In Berlin on 25 October 2026, 03:00 becomes 02:00. 02:30 happens twice. | A run in the repeated hour **runs once**, at the first of the two. This also holds for interval expressions such as every 15 minutes. |

If a job must never be skipped, avoid times between 02:00 and 03:00 in zones with daylight saving time, or use `UTC`.

## Where the zone is shown

- **Job detail** shows the zone with its current UTC offset, and the next run in both your browser's time and the job's zone.
- **Upcoming Executions** shows the job's zone and the next run as local time in that zone.

Exclusion calendars are matched against the local date of the run in the job's zone. See [Exclusion Calendars](30-exclusion-calendars.md).

## Workflows

Workflow schedules are still evaluated in UTC.
