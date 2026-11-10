using AutoFixture.Xunit3;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class BoardReaderTests
{
    [Theory, AutoNSubData]
    public void Creates_the_state_folder_as_factory_ps1_does([Frozen] IFactoryPaths paths, [Frozen] IFileStore files, BoardReader sut)
    {
        files.ReadText(Arg.Any<string>()).Returns((string?)null);

        sut.Load();

        files.Received(1).EnsureDirectory(paths.Home);
    }

    [Theory, AutoNSubData]
    public void No_board_file_is_the_empty_board([Frozen] IFactoryPaths paths, [Frozen] IFileStore files, BoardReader sut)
    {
        files.ReadText(paths.BoardPath).Returns((string?)null);

        Assert.Equal(Board.Empty().ToJsonString(), sut.Load().ToJsonString());
    }

    [Theory, AutoNSubData]
    public void Reads_the_board_file([Frozen] IFactoryPaths paths, [Frozen] IFileStore files, BoardReader sut)
    {
        files.ReadText(paths.BoardPath).Returns("{\"version\":2,\"jobs\":[{\"id\":\"J-001\"}]}");

        Assert.Equal("{\"version\":2,\"jobs\":[{\"id\":\"J-001\"}],\"ideas\":[]}", sut.Load().ToJsonString());
    }
}
