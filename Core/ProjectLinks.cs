namespace ClickClean.Core;

public static class ProjectLinks
{
    public const string Author = "离子怪";
    public const string Blog = "https://ice11123.github.io/blog_test2/";
    public const string AuthorGithub = "https://github.com/ice11123";

    public static Uri? Resolve(string? key, string repository)
    {
        var value = key switch { "repository" => repository, "blog" => Blog, "author" => AuthorGithub, _ => null };
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            ? uri : null;
    }
}
