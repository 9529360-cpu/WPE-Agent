using WpeAgent.Notifications;

namespace 币安量化机器人.Services.Agent;

public sealed class TeacherLessonNotificationPublisherV2(AgentSqliteStore store,IConfirmedNotificationObserver observer,Func<NotificationEventKind,bool> isAllowed)
{
    public async Task<bool> PublishIfAllowedAsync(TeacherLessonV2 lesson,string provider,string environment,CancellationToken ct)
    {
        var kind=lesson.Kind switch{TeacherLessonKindV2.Morning=>NotificationEventKind.TeacherMorningLesson,TeacherLessonKindV2.Afternoon=>NotificationEventKind.TeacherAfternoonLesson,TeacherLessonKindV2.Evening=>NotificationEventKind.TeacherEveningLesson,TeacherLessonKindV2.Event=>NotificationEventKind.TeacherEventLesson,_=>throw new ArgumentOutOfRangeException()};if(!isAllowed(kind))return false;
        var deliveryId=$"{lesson.LessonId}:{kind}";if(!await store.TryQueueTeacherDeliveryAsync(deliveryId,lesson.LessonId,kind.ToString(),DateTimeOffset.UtcNow,ct))return false;var content=string.Join("\n\n",lesson.Blocks.Select(x=>$"【{x.Heading}】\n{x.Content}"));if(content.Length>3500)content=content[..3500];var eventKey=ConfirmedNotificationTruth.EventKey(lesson.LessonId,lesson.ContentSha256,kind);await observer.ObserveAsync(ConfirmedNotificationTruth.System(eventKey,kind,provider,environment,lesson.GeneratedAtUtc.UtcDateTime,"teacher.lesson.v2.ready",content:content),ct);return true;
    }
    public async Task<bool> PublishRecommendationIfAllowedAsync(string sourceLessonId,TeacherRecommendationV2 recommendation,string provider,string environment,CancellationToken ct)
    {
        const NotificationEventKind kind=NotificationEventKind.TeacherRecommendation;if(!isAllowed(kind))return false;var deliveryId=$"{recommendation.RecommendationId}:{recommendation.Version}:{kind}";if(!await store.TryQueueTeacherDeliveryAsync(deliveryId,sourceLessonId,kind.ToString(),DateTimeOffset.UtcNow,ct))return false;var content=$"研究候选：{recommendation.Instrument}\n状态：{recommendation.State}\n逻辑：{recommendation.Thesis}\n确认：{string.Join('；',recommendation.ConfirmationConditions)}\n失效：{string.Join('；',recommendation.InvalidationConditions)}\n风险：{string.Join('；',recommendation.MaterialRisks)}\n本内容无交易执行权。";var hash=MarketTeacherComposerV2.Hash(System.Text.Json.JsonSerializer.Serialize(recommendation));var eventKey=ConfirmedNotificationTruth.EventKey(recommendation.RecommendationId+"-"+recommendation.Version,hash,kind);await observer.ObserveAsync(ConfirmedNotificationTruth.System(eventKey,kind,provider,environment,recommendation.IssuedAtUtc.UtcDateTime,"teacher.recommendation.v2.ready",recommendation.Instrument,content:content),ct);return true;
    }
}
