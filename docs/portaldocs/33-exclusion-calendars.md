---
id: exclusion-calendars
title: "Exclusion Calendars"
sidebar_label: "Exclusion Calendars"
sidebar_position: 58
description: "Keep jobs from running on public holidays, maintenance days or weekends with named exclusion calendars."
keywords: [milvaion exclusion calendar, holiday calendar, skip holidays, maintenance window, business days]
---

# Exclusion Calendars

An exclusion calendar is a named list of days on which jobs should not run: public holidays, maintenance days, a company shutdown, weekends. You create a calendar once and reference it from as many jobs as you like.

## What a calendar holds

A calendar can combine three kinds of rule. A day is excluded when any of them matches.

| Rule | Example |
|------|---------|
| Single dates | `2027-01-01`, `2027-04-23` |
| Date ranges, both ends inclusive, with an optional label | `2027-12-24` to `2027-12-31`, "Year-end shutdown" |
| Days of the week | Saturday and Sunday |

Dates are plain calendar dates without a time or a zone. Each job compares them with the **local date of its run in its own time zone**, so a single "Public holidays" calendar works for jobs in Istanbul and in New York alike.

## What happens on an excluded day

When a job's run falls on an excluded day:

1. The run is not sent to a worker.
2. An occurrence is recorded with the status **Skipped**, not Failed. Its result and its log say which calendar excluded which date.
3. The job continues at its first run on a day that is not excluded. A job that runs every 5 minutes on a calendar that excludes weekends records one skipped occurrence for the weekend, not one per run.

A one-time job whose time falls on an excluded day is skipped and recorded the same way, and does not run later.

**Upcoming Executions** leaves excluded days out: if a job's next run falls on one, the list shows the first run after it, marked "after excluded day".

Calendars are read on every dispatcher cycle, so changes to a calendar apply from the next run on.

## Managing calendars

### Dashboard

Open **Calendars** in the sidebar. From there you can create, edit and delete calendars. The edit dialog lists the jobs that use the calendar.

To use a calendar, open a job's edit form and tick it under **Exclusion Calendars** in the settings panel.

A calendar that is still used by a job cannot be deleted. Remove it from those jobs first. This keeps a job from silently starting to run on days someone chose to exclude.

### API

| Method | Endpoint | Permission |
|--------|----------|------------|
| `PATCH` | `/api/v1/exclusionCalendars` (list; supports `searchTerm` and paging) | `ExclusionCalendarManagement.List` |
| `GET` | `/api/v1/exclusionCalendars/calendar?calendarId={id}` | `ExclusionCalendarManagement.Detail` |
| `POST` | `/api/v1/exclusionCalendars/calendar` | `ExclusionCalendarManagement.Create` |
| `PUT` | `/api/v1/exclusionCalendars/calendar` | `ExclusionCalendarManagement.Update` |
| `DELETE` | `/api/v1/exclusionCalendars/calendar?calendarId={id}` | `ExclusionCalendarManagement.Delete` |

Create a calendar:

```json
POST /api/v1/exclusionCalendars/calendar
{
  "name": "Public holidays 2027",
  "description": "National holidays",
  "dates": ["2027-01-01", "2027-04-23", "2027-05-01"],
  "dateRanges": [{ "start": "2027-12-24", "end": "2027-12-26", "label": "Christmas" }],
  "weekdays": []
}
```

`weekdays` uses 0 for Sunday through 6 for Saturday. `PUT` replaces the whole calendar, so send every field, including `id`.

Reference calendars from a job with `exclusionCalendarIds`:

```json
POST /api/v1/jobs/job
{
  "displayName": "Daily settlement",
  "selectedJobName": "SettlementJob",
  "cronExpression": "0 0 18 * * *",
  "timeZoneId": "Europe/Istanbul",
  "exclusionCalendarIds": ["01a1d700-548a-7fd1-9f68-b01b6027b982"]
}
```

On `PUT /api/v1/jobs/job`, `exclusionCalendarIds` replaces the job's whole list.

### MCP

The MCP server has `list_exclusion_calendars`, `get_exclusion_calendar`, `create_exclusion_calendar`, `update_exclusion_calendar` and `delete_exclusion_calendar`, and `create_job` and `update_job` accept `exclusionCalendarIds`.

## Permissions

The five `ExclusionCalendarManagement` permissions are added to the permission list automatically when the API starts. Grant them to roles the same way as any other permission. A user who can edit jobs but cannot list calendars sees no calendars on the job form.

## Limits

- A calendar can hold up to 1,000 single dates and 1,000 ranges.
- A range can span at most 3,660 days.
- A job can reference up to 20 calendars.
