using System;
using Microsoft.Playwright;

namespace PercyIO.Playwright
{
  public interface IPercyPlaywrightDriver
  {
    string GetUrl();
    string GetSessionId();
    string GetPageGUID();
    string GetFrameGUID();
  }
}
