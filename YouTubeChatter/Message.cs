namespace YouTubeChatter
{
  public class Line
  {
    public string TimeAuthor { get; set; }
    public string Message { get; set; }

    public Line(string timeAuthor, string message)
    {
      TimeAuthor = timeAuthor;
      Message = message;
    }

    public override string ToString()
    {
      return $"{TimeAuthor}: {Message}";
    }
  }
}