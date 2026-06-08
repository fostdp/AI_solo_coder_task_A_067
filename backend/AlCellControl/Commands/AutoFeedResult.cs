namespace AlCellControl.Commands;

public record AutoFeedResult(bool Success, long FeedingRecordId, long CommandId);
