using Classroom.Contracts;
using Classroom.Client;
using Classroom.Desktop.Electronics;
using System.Net;
using System.Reflection;
using Xunit;

namespace Classroom.UiTests;

public sealed class SpiHistoryTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static ElectronicsSpiIo Sample()=>new("tft",0,false,11,13,12,1,1,1,1,0,0,0,0,8,4_000_000,false,0,0,0.002,[0x2c],[0],[0x2c],0xe0da836e,0xd202ef8d,[]);
    [Fact]
    public void MetadataSurvivesRecordReplayAndSealedPagesWhileOldChangesKeepTheirEncoding()
    {
        var json=TftNativeTests.Project("uno").Serialize();var initial=ElectronicsJournal.Start(json,1);var old=ElectronicsJournal.Record(initial,json,json,"work-open",2,force:true);
        var oldJson=Wire.Write(old);Assert.DoesNotContain("spiIo",oldJson);Assert.Equal(oldJson,Wire.Write(Wire.Read<ElectronicsJournal>(oldJson)));
        var sample=Sample();var journal=ElectronicsJournal.Record(old,json,json,"spi-transfer",3,force:true,spiIo:[sample]);Assert.Equal(json,Wire.Read<ElectronicsJournal>(Wire.Write(journal)).Replay());Assert.Equal(Wire.Write(sample),Wire.Write(journal.Changes[^1].SpiIo![0]));
        var root=Path.Combine(Path.GetTempPath(),"classroom-spi-history-"+Guid.NewGuid().ToString("N"));var context=new ElectronicsHistoryContext(Guid.NewGuid().ToString("N"),new string('0',64),Guid.NewGuid().ToString("N"),null);
        using(var history=new ElectronicsHistoryStore(root,context,json))for(var i=0;i<65;i++)history.Record(json,"spi-transfer",true,spiIo:[sample with{StartedMs=i,EndedMs=i+0.002}]);
        using var reopened=new ElectronicsHistoryStore(root,context,json);Assert.True(reopened.Reopened);Assert.Equal(65,reopened.Journal.Changes.Length);Assert.Equal(json,reopened.Journal.Replay());Assert.Equal(Wire.Write(sample),Wire.Write(reopened.Journal.Changes[0].SpiIo![0]));Assert.Single(Directory.GetFiles(root,"p-*.json"));
        var text=SpiDiagnostics.Describe([sample]);Assert.Contains("не полный поток пикселей",text);Assert.Contains("Календарное время",text);
    }
    [Fact]
    public void InvalidCountersPinsDevicesAndPayloadSizesAreRejectedOnReplayAsWellAsRecord()
    {
        var json=TftNativeTests.Project("uno").Serialize();var initial=ElectronicsJournal.Start(json,1);var good=Sample();
        var invalid=new[]{good with{DeviceId="board"},good with{Words=2},good with{TxPrefix=new int[33]},good with{RxPrefix=[256]},good with{Mosi=20},good with{Sck=11},good with{Controller=1},good with{EndedMs=-1},good with{ClockHz=double.NaN},good with{CompletedWords=0},good with{Commands=2},good with{ParameterChanges=1},good with{Issues=["punishment"]}};
        var valid=ElectronicsJournal.Record(initial,json,json,"spi-transfer",2,force:true,spiIo:[good]);
        foreach(var sample in invalid){
            Assert.Throws<InvalidDataException>(()=>ElectronicsJournal.Record(initial,json,json,"spi-transfer",2,force:true,spiIo:[sample]));
            Assert.Throws<InvalidDataException>(()=>(valid with{Changes=[valid.Changes[0] with{SpiIo=[sample]}]}).Replay());
        }
        Assert.Throws<InvalidDataException>(()=>ElectronicsJournal.Record(initial,json,json,"code-edit",2,force:true,spiIo:[good]));
        Assert.Throws<InvalidDataException>(()=>ElectronicsJournal.Record(initial,json,json,"spi-transfer",2,force:true,spiIo:[good,good]));
    }
    sealed class Receiver(string mode):HttpMessageHandler
    {
        public int Capabilities,Payloads;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            if(request.RequestUri!.AbsolutePath.EndsWith("capabilities")){
                Capabilities++;
                return mode=="old-capability"?new(HttpStatusCode.NotFound){Content=new StringContent("{\"error\":\"old server\"}")}:new(HttpStatusCode.OK){Content=new StringContent(Wire.Write(new ElectronicsJournalCapabilities([ElectronicsJournalProtocol.SpiSummary])))};
            }
            var batch=Wire.Read<ElectronicsJournalBatch>(await request.Content!.ReadAsStringAsync(cancellation));if(batch.Changes.Length>0)Payloads++;
            var received=mode=="drops-spi"?batch.Changes.Select(c=>c with{SpiIo=null}).ToArray():batch.Changes;
            var last=received.LastOrDefault();var hash=mode=="old-head"||mode=="legacy"?null:SqlSyncHash.Text(Wire.Write(received));
            return new(HttpStatusCode.OK){Content=new StringContent(Wire.Write(new ElectronicsJournalAck(batch.StreamId,"accepted",mode=="ahead"?20:last?.Sequence??0,last?.AfterHash??SqlSyncHash.Text(batch.BaseJson),hash)))};
        }
    }
    [Theory]
    [InlineData("old-capability")]
    [InlineData("old-head")]
    [InlineData("drops-spi")]
    [InlineData("legacy")]
    [InlineData("ahead")]
    public async Task OldOrDroppingReceiversNeverClearSpiQueuesWhileLegacyHistoryStillUploads(string mode)
    {
        using var receiver=new Receiver(mode);var connection=new ConnectionSettings("https://127.0.0.1",new string('0',64));using var api=new ClassroomApi(connection);
        var oldHttp=(HttpClient)typeof(ClassroomApi).GetField("http",Private)!.GetValue(api)!;typeof(ClassroomApi).GetField("http",Private)!.SetValue(api,new HttpClient(receiver){BaseAddress=new Uri("https://127.0.0.1/api/v1/")});oldHttp.Dispose();
        var profile=Guid.NewGuid().ToString("N");typeof(ClassroomApi).GetProperty(nameof(ClassroomApi.ProfileId))!.SetValue(api,profile);
        var root=Path.Combine(Path.GetTempPath(),"classroom-spi-capability-"+Guid.NewGuid().ToString("N"));var json=TftNativeTests.Project("uno").Serialize();var context=new ElectronicsHistoryContext(profile,connection.Fingerprint,Guid.NewGuid().ToString("N"),null);
        using var history=new ElectronicsHistoryStore(root,context,json);for(var i=0;i<(mode=="ahead"?20:1);i++)history.Record(json,mode=="legacy"?"work-open":"spi-transfer",true,spiIo:mode=="legacy"?null:[Sample()]);var before=Wire.Write(history.Journal);var client=Guid.NewGuid().ToString("N");
        if(mode=="legacy"){Assert.True(await history.Deliver(api,client));Assert.False(history.Pending);Assert.Equal(0,receiver.Capabilities);Assert.Equal(1,receiver.Payloads);}
        else if(mode=="ahead"){Assert.True(await history.Deliver(api,client));Assert.False(history.Pending);Assert.Equal(20,history.DeliveryState!.Acknowledged);Assert.Equal(3,receiver.Payloads);}
        else{Assert.Contains("сервер",(await Assert.ThrowsAsync<InvalidDataException>(()=>history.Deliver(api,client))).Message,StringComparison.OrdinalIgnoreCase);Assert.True(history.Pending);Assert.Equal(before,Wire.Write(history.Journal));Assert.Equal(0,history.DeliveryState?.Acknowledged??0);Assert.Equal(mode=="drops-spi"?1:0,receiver.Payloads);}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataPacketsRemainIdempotentAcrossOverlapAndConfirmedReopening(bool receiptLost)
    {
        await using var f=await LiveClassroom.Start();var capabilities=await f.Student.Get<ElectronicsJournalCapabilities>("electronics-journal/capabilities");Assert.Contains(ElectronicsJournalProtocol.SpiSummary,capabilities.Features);
        var json=TftNativeTests.Project("uno").Serialize();var client=Guid.NewGuid().ToString("N");await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(client,1,json,[]));
        var root=Path.Combine(Path.GetTempPath(),"classroom-spi-packets-"+Guid.NewGuid().ToString("N"));var context=new ElectronicsHistoryContext(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null);string stream;
        using(var local=new ElectronicsHistoryStore(root,context,json)){
            for(var i=0;i<20;i++)local.Record(json,"spi-transfer",true,spiIo:[Sample()]);
            if(receiptLost)for(var i=0;i<20;i+=8)await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync",new ElectronicsJournalBatch(local.Journal.StreamId,client,context.LessonId,null,local.Journal.StartedAt,json,20,local.Journal.Changes.Skip(i).Take(8).ToArray()));
            for(var i=0;i<4&&local.Pending;i++)await local.Deliver(f.Student,client);Assert.False(local.Pending);stream=local.DeliveryState!.StreamId;
            var packet=new ElectronicsJournalBatch(stream,client,context.LessonId,null,local.Journal.StartedAt,json,20,local.Journal.Changes.TakeLast(8).ToArray());
            var ack=await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync",packet);Assert.Equal("accepted",ack.Status);Assert.Equal(SqlSyncHash.Text(Wire.Write(packet.Changes)),ack.ChangesHash);Assert.Equal(ack,await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync",packet));
        }
        using(var reopened=new ElectronicsHistoryStore(root,context,json)){Assert.True(await reopened.Deliver(f.Student,client));Assert.False(reopened.Pending);Assert.Equal(stream,reopened.DeliveryState!.StreamId);}
        var streams=await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}");Assert.Single(streams);var saved=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream}/changes");Assert.Equal(20,saved.Events.Length);Assert.All(saved.Events,e=>Assert.Equal(Wire.Write(Sample()),Wire.Write(Assert.Single(e.Change.SpiIo!))));
    }
}
