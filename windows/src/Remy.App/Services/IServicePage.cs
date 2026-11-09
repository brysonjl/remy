namespace Remy.App.Services;

/// <summary>Implemented by every settings page. The composition root calls Attach once after services exist.</summary>
public interface IServicePage
{
    void Attach(AppServices services);
}
