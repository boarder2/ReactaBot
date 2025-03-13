# ReactaBot Database Schema

This document provides an overview of the SQLite database schema used by ReactaBot to track message reactions and manage scheduled reports.

## Database Tables

The ReactaBot database consists of five main tables:

### 1. `messages`

Stores information about Discord messages that have reactions.

| Column | Type | Description |
|--------|------|-------------|
| `id` | `BIGINT` | Primary key - Discord message ID |
| `guild_id` | `BIGINT` | Discord guild (server) ID |
| `channel_id` | `BIGINT` | Discord channel ID |
| `parent_channel_id` | `BIGINT` | Parent channel ID (for threads/forum posts) |
| `author` | `INTEGER` | Discord user ID of message author |
| `url` | `VARCHAR(300)` | Jump URL to the message |
| `timestamp` | `INTEGER` | Message creation timestamp |
| `total_reactions` | `BIGINT` | Total number of reactions on the message |

**Indexes:**
- `messages_guild_id_author` - on `guild_id` and `author`
- `messages_guild_id_timestamp_total_reactions` - on `guild_id`, `timestamp`, and `total_reactions`
- `messages_guild_id_channel_id` - on `guild_id` and `channel_id`
- `messages_guild_id_parent_channel_id` - on `guild_id` and `parent_channel_id`

### 2. `reactions`

Stores individual reactions for messages.

| Column | Type | Description |
|--------|------|-------------|
| `id` | `INTEGER` | Primary key - Auto-incrementing ID |
| `message_id` | `BIGINT` | Foreign key to `messages.id` |
| `reaction_count` | `INTEGER` | Number of this reaction on the message |
| `emoji` | `VARCHAR(50)` | Emoji name/identifier |
| `reaction_id` | `BIGINT` | ID for custom emoji (NULL for Unicode emoji) |

**Foreign Keys:**
- `message_id` references `messages(id)`

### 3. `opted_out_users`

Tracks users who have opted out of reaction tracking.

| Column | Type | Description |
|--------|------|-------------|
| `user_id` | `BIGINT` | Primary key - Discord user ID |
| `opted_out_at` | `TIMESTAMP WITH TIME ZONE` | When the user opted out |

### 4. `scheduled_jobs`

Stores scheduled top message reports.

| Column | Type | Description |
|--------|------|-------------|
| `id` | `TEXT` | Primary key - GUID for the job |
| `cron_expression` | `TEXT` | Cron expression for scheduling |
| `interval_hours` | `REAL` | Time interval to analyze in hours |
| `channel_id` | `BIGINT` | Discord channel ID to post results |
| `guild_id` | `BIGINT` | Discord guild (server) ID |
| `count` | `INTEGER` | Number of messages to show |
| `next_run` | `TIMESTAMP WITH TIME ZONE` | Next scheduled run time |
| `created_at` | `TIMESTAMP WITH TIME ZONE` | Job creation timestamp |
| `is_forum` | `BOOLEAN` | Whether to post in a forum channel |
| `thread_title_template` | `TEXT` | Template for forum thread title |

**Indexes:**
- `scheduled_jobs_guild_id_channel_id` - on `guild_id` and `channel_id`
- `scheduled_jobs_next_run` - on `next_run`
- `scheduled_jobs_id` - on `id`

### 5. `schedule_channels`

Stores channel inclusions/exclusions for scheduled jobs.

| Column | Type | Description |
|--------|------|-------------|
| `schedule_id` | `TEXT` | Foreign key to `scheduled_jobs.id` |
| `channel_id` | `BIGINT` | Discord channel ID to include/exclude |
| `is_excluded` | `BOOLEAN` | Whether this is an exclusion (true) or inclusion (false) |

**Primary Key:**
- Combined primary key on (`schedule_id`, `channel_id`)

**Foreign Keys:**
- `schedule_id` references `scheduled_jobs(id)` with CASCADE DELETE

**Indexes:**
- `schedule_channels_schedule_id` - on `schedule_id`

## Database Version Management

The database uses SQLite's `PRAGMA user_version` to track schema versions. The current version is **4**.

Version history:
- Version 1: Added `is_forum` and `thread_title_template` columns to `scheduled_jobs`
- Version 2: Converted `interval` string to `interval_hours` numeric value in `scheduled_jobs`
- Version 3: Added `parent_channel_id` column to `messages` table for tracking thread/forum relationships
- Version 4: Added `schedule_channels` table for managing channel inclusions/exclusions in schedules

## Data Flow

1. When a reaction is added/removed from a message, the `UpdateMessageReactions` method:
   - Updates or inserts the message in the `messages` table, including parent channel info for threads
   - Deletes existing reactions for that message
   - Inserts current reactions into the `reactions` table

2. For top message reports:
   - `GetTopMessages` queries both tables to retrieve messages with their reactions
   - Results can be filtered by channel, parent channel (forum), user, and time range
   - Results can be filtered by included/excluded channels for scheduled jobs
   - Results are sorted by total reaction count

3. Scheduled reports are managed through the `scheduled_jobs` table:
   - Jobs are configured with a cron schedule and output channel
   - Channel inclusions/exclusions in `schedule_channels` determine which channels to analyze
   - The `SchedulerService` checks for due jobs and applies channel filters when gathering messages

4. Users can opt out of tracking via the `opted_out_users` table, which deletes all their existing data

## Security and Privacy

- Messages without reactions are automatically removed from the database
- Users can opt out at any time, which removes all their tracked data
- Only message IDs and reaction counts are stored, not message content directly