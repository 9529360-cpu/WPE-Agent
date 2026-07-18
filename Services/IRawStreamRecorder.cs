namespace 币安量化机器人.Services;

public interface IRawStreamRecorder
{
    void Record(string channel, string rawMessage);
}
