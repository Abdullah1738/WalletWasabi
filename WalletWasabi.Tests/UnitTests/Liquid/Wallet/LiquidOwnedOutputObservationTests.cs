using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidOwnedOutputObservationTests
{
	private const string SpendPublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string AlternatePublicKeyHex = "0379be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string RustTransactionIdHex = "35ab905fc934c08fa976d55427bdd3970383e0f01ece059426ec04144b4ecc3d";
	private const string RustWitnessBindingHex = "78ee7e96e486b0fbe2ad4df5820fe00f4c77b0c7475562bf9bf31871d3294e01";
	private const string RustWitnessInclusiveTransactionHex =
		"020000000102a2fb2fd3085d34848af57f14793f7111614a19f4c5f616f19dbd270a3579b5620000000000ffffffffa2fb2fd3085d34848af57f1479" +
		"3f7111614a19f4c5f616f19dbd270a3579b5620100000000ffffffff030ab1a280376fd4808b975d96dd014d55ddc30ea968b0ad3cc9669263901963" +
		"259409068b020cd36bb8385c63e5877decfaeefd7caf92e48b1940af9ae1d3e41022be03e3e14208f811d2ad9580b5ba5a4ed4fe8cb2ce0a9bab6434" +
		"b73121e9ba36b457160014d363d538bea12647f61c634bdd7a791d676850e90b14128a5283247ac76682c27c743f4a20153eff99603cacf2acb5b6e6" +
		"9c8da2cb086b79028cad505bdbad67b3840e86011f77940b449d474cc95ac2b5b9123c633603f50619c1b0c5484c6a56ec2061af3ba5d2981df8cb42" +
		"f19813c5e22a6597b875160014a3e18f06b5369914234bd7df7462d7bbd363571401000102030405060708090a0b0c0d0e0f10111213141516171819" +
		"1a1b1c1d1e1f010000000000000064000000000000000000000000000063020003148d31a4051b89bd28044c5947edfe12339986168000aee84dad7b" +
		"41be7dc9c98fa768df64e4a24ea51cfa73d6d613de380a609b618e1c22b176f7ca4f0c89b1888dac2e64dfaf0df10e247a8d1a350d191c8869ee8880" +
		"4ee13faebaa18ec372fd4e10603300000000000000015149fd00dc771ed3ef11294aa9dd8073ecf6aa5784a62160b183dcb7b598aab76ea8d0aae7d7" +
		"ac05709c6e2f14bb45dd9f63e700843eea5f2d761198ba6ce7289a5246d943c9f49bad776a73b01bc3b3d756f9ef4f5f2389b5bf9dde1b8160f4bc71" +
		"7ecd324849dc3d4fada6944663daedcb89abb563432527ab2bf4eb146bd8b3ec0ca0b58287a2c57a370494448bf4e4cedc5a6518ef9df46399e20f0d" +
		"9881c17be122ed23e8bf508c74ffd3bd475b91630a19524fbd4ccfee5ec562603357298a206417ec618acf0838edbe7f8cd4aa5b3de063408c65cbd4" +
		"e8361e227dc22662ac4f024e3bcf2a811f7ea0231974c29ce063d5ba18df9c1c5ba6f612b82c00e7b3598f688ed023ad1fa011aba9cbf842ecfe7602" +
		"0f61e8c1e72cfdf89f5cc7d9b685502971710e195c24a570cb55dc74e7784df86652ae2a4234c5ea22f7eec3fb891ae72e12c9d3c83c0987bfc24d79" +
		"2367bb137637a514c51d1e3be4737c3a36f6fae33ff179692f325d9761fc84f8f9604b7389b95888fd4b824f1b9216a1862026886c62e2876f9d7fb2" +
		"040d9351ae0fac46e44a9a3222f08a8d8f1f643f0cbfc51a39b5c9d6af91b24377d50c22ac1397d2cec5e494dbc2c3de3a0a8ab8ccc2ce2f3253d0af" +
		"24ea9a15d789a25c9f3c799bbd51fe0eecec382976ef94bcb03faf1505e4baf5c978fba9d487389cfa105ba4c75bf20c5ca4f2b489f11ebb2a369929" +
		"3d6ab86c8c0edb8916f5f003eaaaec209a897433d22b10a81581a64cbe461846f548db4b0b3e4687d12fd32814338761122a6b97d92e92a823bcf7d8" +
		"6d28020eed5b270a56a0b9ebcb19a1804c478af73f828c12769dff94503dfc6d516a00e1e968764d57058239b17a1e91c4a87eca8b9900cb5511b69c" +
		"a145e2aa954b699203ce53dda21986654aa1d5dce497cb551bef0061b569d7f047cb1f3f4a2c89a9c1e6a5cb49f0df708f39aa6a4208f297f7529c2c" +
		"48473b4e0bd1ab86d91192e41404a9bba1837e47608d3034166665cd54a4512bb10d952f181fc175f9133080eef03a712f247b01c19439c5640f21a4" +
		"11e1428afdaa7700ae0aaec0251620815ae5e8192cb4693b19787b0b8bca71ff41edb2f11dc0ab1c113c2e90c402b0a39fbe1207a4ba627e15d41b5e" +
		"4151be71550ee160d3609c437fcdbf62d71b4be18cca2a02d4a0e4b38860023f0239839c8cbee15d67d0f847fec2915dd4971bbe0c02301ca8852b6f" +
		"45f02dd64e41451115baf57a13fa0ba68c68c736ef87f660f8dcd3dbe09b8b0b4bdef6a82d5c6f02fa021b4b4cfb323f410e9fde46ec478fd35b4e32" +
		"519b9bb00003fe1a009c03af6a64b74adb9007d2accd16d95cfd436b6938668b5cb40dbe18e6c4f16a67b16084f5f5b40d7ae9f66d8f22e553a5ac33" +
		"9750eec4204cc64f417c379f0d485e6849aeac932a6afaa3508327fbf7c405834d3b12805a5fef726c18198f229bfe4b12201297bd50d4749377b1b4" +
		"6861b85f123bd41d36b16a56359ca828141891037f9b105197b5db1787b8b5acb7bf00146f2263b6e2c01f3c439de80431feec6382a33e1adab87dfd" +
		"1a169b22c582d1e4c8a5f964ad49ea8b22eafd14fbc52992215317ee7da3d03abb150ed0c2cfbdcdf01798665ec6c2caa3746b6782ec8491ab6b1f15" +
		"e87660d21c8d3bf09b6b60e70f6646c8267cf8f8c6f41b75cbe5edadf0632a98abd2ca560496d8da0a09976e667d5508462bab7f94111bb1cde13ba1" +
		"088ff3e1206069ac96316f1742974b3d95a66dbec02257cd40f0c74d19bec04c43a90adc73e6756ee5ecff4d7cfdd8f0223163f59820ac9aa4155b10" +
		"215850abf7e999dcdc17bbab04426baa6e4f3bc08ad3f8c76d6068fa52b12a592618c40a7064969a984f6392506a5d1e097e9ef7a47823ad80f044c7" +
		"04177b402770f2be9310bfc3f4dbdb25962d05d3c56f3eb4b069e5c379c92d7fdbfb368435ab9687fef30998a205d06211904521f454e6cc20b803e6" +
		"f54f0247723f89a1a534e9f30de6d1b7f32036cf20419c1ca526a1fa55f6a56dca4c78e45efbee42d38e24b9c4b885753001405efbe9923f306bc278" +
		"2863730d40f45517b1b82f706f2fd15a0fb1b8c0be7e4568ad0aff8f6acfd03d07ccdaabc2b42bd527f2e8a91a4dfffcecced3358d77bb8fc03dbac0" +
		"a13d4fee87a535dbee191ccc2159de88ea7ed00cfd92fa00a63f4c6c4a48cbaeae8be6d403d02f0376355a1980aeb87c3d69d11f9f21f460d89a3716" +
		"22e0facbd24c0ee6e7feff9727e3db9a6c1b2d77e33d6abac869e28976efa2a1041849fd5a0d4c5413c3d8b925e89ca081f4918a68993bc5d05ac3c9" +
		"df6ff0b08803bbebe7b6d8c6dfe9fec42a624cbf9e1469020c6ef780680130c8f3427a59137af676750b6054f163edeb8980a6f7f2f1c5cdeb35da95" +
		"e217ce9bc731147deb7a8160f6160401b586f49716654f9861698930a307ad6d5c812063165f8beab0350793263b2081402d375f04ba326bcb5d0b0a" +
		"c4c6ed36fa956dbdba49ed28eaec832ffbefa1e78a008f34df0336035f09844b354eb15363fff1c9d9b7ef93ff2c4465a0b84ec38aad2b2cb30cc104" +
		"6397c6664994f82e9261d5b2bf3e7e8154b7e96522cea9035b0f900a9ae59e29980dd52bcc773ff788bd7317fd078ed2a65b5ac2e95aba0bf785cbd8" +
		"f30babf02531a8138339a91eaf6e65a055e24ffb2c2b064e90faa027a2d220d8b9667ef5bf4f0a4dd67cd7fb85b8717a7c4417840092030d66ac913c" +
		"3171577d2822b82229e88df0b7ae2e292c29a338201e00571e73ba44043549f91298673b67e159616b20ff2146e43e23d7aa0d6e7690dc6b0c694f75" +
		"363df70c1e8b9cdcb2d9297d926e440c745ab726fa7e6f6518168e0527d401fac5f05c91a6136bdf2d6d8ff02e7ba599e62497efcdd4a3083c6978e5" +
		"f5b7b8492880bf860be80bf2ceb4be984e111c369ec52602842def53dded4d28cfad7f61366f702119376014ff406f921c89644de6c3a2a87ae6320e" +
		"eeee12f9bfaebb64bdf8583c0989fba79df100137393703d644879a90f3e14418cd7b9c218a8c0ce19cb7e8ede67a7e4697cbd1e4170f6a20b85c5bd" +
		"019d645afab9449f744db040f774626e39f6c63e65366e0c67f866c8e858a149cc8587d162b74cec8bcecf0a7021cc518457641ee099a059d41d757d" +
		"92e33e37992482b2910d8b46e0afe34b8e66e8a773e68d2b49f18365483700f30f082d436be99da944f2a7c2c375f6be5c984c4897597dd4db3d84c3" +
		"01804b5002540ea20595646440dc54652dc1018fcdacc61b2b07c594960438f8cd0ae2e3047b5d31e4a67f2e980e976e3fcee779bb20cc57d7fabaeb" +
		"8bed01e506fa73886efc4904a3dfcfcfc2d3b3acbd4035ff3bcbc6607bbed25b54716438e3a25e2c258695ef4776875d04016083f5cdd17b4a2b35db" +
		"2125415a34d37e5b6414a386468e012006d5722f14bf9c040ea1c84009921f214dbdbb6cde6e64088c45e772e84ba873332c5797fbfea1d1a8ebb719" +
		"d332bd7c4f2b8fb148e462309db2b579102cb95f1af48c14b9b0008707bf646833444b85a86c02d954ea218c914d8f517200908f5760eaec6b31e82b" +
		"eed896286a2bb22ba5ce84bd1ac0a8508f46e9436c7ac09b60310a7fc2f2c2357dac466e0ba616865aa383d5551e19e13178bcdbaedeaf2044c1fa02" +
		"91f1cd66aac6a7e0b0412d8be50d537dfa14b135b1e218f60f0e2ef35ec1013b32207f11bbe7b0a658638f414dd0b28f09ec751ce9e37f0430af946a" +
		"7d0fb018b4c1f6d470c9c51a1a167d2a0a0a85a7383c8e6b4ac7bcd711fcd0cde1f7b393f78a4acef71482d2b7748602decb74c591da8e293e08f0ad" +
		"dbc6d1fa664b8eaa45e4df8815337ac5923d03163a6fbbfa9b56ac2b50aaf11743eccd2f0f5e846da64ecaa14db2b9b3e779b6d91a27c3d430bf19da" +
		"d89c846e653de7990ba905c4fd61db8a5faf63a026e51d9d85a840fe56c81840c5961d751dec992be40581df77082371cb9d375b2faab55c306c37a7" +
		"0da95b2dc51eefa4c388021ced324f44cd5b0c75b9d2fde3ac87a6260cc35acf9d333648cd91f2251e8118a5fa67dc4c54da43e6746af5eeb5e802e3" +
		"f90f2a8e50adf7db984fa856940155082507d5e6489554faea260a6746102fa345fd37da66c28960faa89df18cb42dc9dbee38371cb1c17db5aa3827" +
		"f5c61d7baf538fd3a2058bd1101389f8b0ee78c7b9ab8fe2379cef7ffd28f85721913aaeb6e30b3b661146368238d2033ad6da2b30be74c1d6a63fd2" +
		"1ff1f06334a8f56273cb2262deb49cf4ba95d6d3d80a3b88f9fd8e8510ed473c4da5f514a870bdc712baf0d7cb14c9bf007f0aff83eeb7cb3da48a0d" +
		"5e67274fc1b68d349624a1c8cc25e13576ddda65e31235094e5d53762071ef1790991844a3400b6a868d696af9484284e561d3ca29b83f75a9733923" +
		"1d88f55cd98b331702776aed23da0787937473eae67206a324a5e883e4473573b0e27ae90419e094d1d326a8f1da3675579be2e410ab10e652dcffc2" +
		"67f68051066a24398d7adfb453e9e109e46d5138336c400b1130bd778d7fe245a9dd86beb35f4efabedf62a21d0c663d840a7288f385267f24a53dbb" +
		"d7e7413d4365b0514e10f4e521ff183fe61d0cfe89fc930924c9b90d22ca5a0b3bb89b5ca80d66849b86df05cddb3c8711380892a0bac02cc142b0ba" +
		"fa8591205c4b00ed126091bd97b49ec871501758a35116bf5c835f29c923f681f338908f068540a6d02b15d0670c17d3464f6818584c8d46968c6331" +
		"b1157ed818b2497569fd17866b32ab7fd823cdf62b0662f66a8d521d4e2acf2b4d5a93bb5c6e3a26595ba6b11f0e95e19d3058d5e8ae10d19851a848" +
		"9f7ed8155cf40230ed809df027bb1ceb7843b0cd7c3dbe0a7e840923ad6329375043a45fe6959a7d260effb568fddee84f78cd292ea5b60e173445f9" +
		"45ef7cc354ab682a38df26e38a4eaf3baa6c39b427daa75eba2230475d0ec30db3ba74f62cdb8cc67371de31681768b4e3ec692ee67886afdf9b93e6" +
		"5292b65ff4683851713c004c5913612096c40867d86d14ae88ab52040024010f22b28c75800850eca41b0aa6c310a504b1619b708b5a285a44d3fcc0" +
		"f85b056e0d6d2282ea12f160f4bdf22c9ada50bf57bd24193cfc392c10e73bbb7a4d921c2ce38bfc0bfe24abcd49d46352be09ff6515351f13df7ca6" +
		"e22545eae9c284cb4a82a1a5f0288fdc4e48db8da874b537045d898cb165d7091f24e578f20c47fe92e0803f4d153540f22a2ced19ca925c5fc06e9b" +
		"3585dd4dc35a01370c6937b38923e5294cf2fb727ce8b2086c5cbe2ea74c0b867476ebe81af198706222defc29b401414688015c05d17b931f75ac5d" +
		"8e888b0b14967cb68ecb8a9f6d5b9e1d9ac1fa4c4f61d4653b4e52048fecaf9fdff8174fea9020e6b074c8c211242e93640143487d387ccfeb5838f0" +
		"d74787f05c9b4a09356e15b2c5ebc6d5d597f4b855687d23c02d90d6dc872298ceee55a772b67ada8ed00cdfdeb1cc446de28878b3fcce9894675a89" +
		"ba4a2dcdf45db501a42cd10ebce803cfdc97abf3f7dcb0c045eecbb9c805f10af327a629e438e3f5ff1a8ee6f6065dae4bf5fd812c3bba87c9edf0b8" +
		"3ee42480a21cc128e261e35bd6f8fd34f5bd24b18b623f66b8d99902dc63bfa4b23bcc37a7382226c0d56a1def99f67bee76ecf33265fbdf5fb33f56" +
		"2b6023ca1ecbabeab4aad7582d6ba000221eb62dd88a6f382acf8c861738cdc347fc6d468b3d3bfc2e19d9d00552318d8399631aa3937bd493b2bf34" +
		"1e4c924c05c6ebe23f11e5ed46659da39597c5cc873709b054d4baf13e33bc4073af23392705f3d48055a1e39cf063020003741e39f26c2f7a637c06" +
		"74cd5165b9d5e4161534f0ae31471ccb23c9342a196e6ce2f432081a36e7ee1ff49a92d611719d009852cf5ecd02f94ed4ceb181c518b207c0abb45f" +
		"18cf9010327f1313a2f392ee6d30d9898f71ae215fde88ffea6dfd4e106033000000000000000149ba200156bacdef9109a3cb72f9761ccbea10743e" +
		"f166a980ce8fce0b4ade0dcbf5b5b71a2b10010b2631a13b971a80e9f3b3c47e0e4a211d5a2db2d397f7e70de3daf3ea0e9316f5372f0cb3090e2af9" +
		"71686e3ea2eabf86c9bd757d0b5a008446987fe366ad2264958762256bd479275fc54795bda0917b81d7b035ca3659227857b233a1b53c3f82fde1dd" +
		"daf45cba3775f57f43b22faa5b5993ed25d48e0c9063ecc448a0f89ab74b5f8c4b7f7a8889decfd250eb7c30a1ae2198958d4e5c425fd0a043b8f2c1" +
		"a9c802df37cc60b6c50a6769187f0630d72057d05b39dde78da55dd566baa5368cef339ae453d29a1454fe75025229a0e32e68944a76cd9937304ebd" +
		"8b3e0d04dd90e0ac1c2cc2f69e2b225cd7987cec3b70fffd789ec8e2d7baf3f53ced54de0db4b20ba281482a3ba731d837ffa2d02e220aec12d87924" +
		"645de9a4b7652364d3afe4fdd5bf01daa83dc0da5bfd4010e7660957d5708ae70cf66dd03c1a859bb1f5cade5c695f268f09f12ea52b90fd96c47725" +
		"e3b6beb92731680373c00e4700e84a2e67b54e3e1251387d1dd5f3c9eb8bc11fa309e4e453ab0ecdd135db7ccca4198a4e6939382dd9fb3ede5d1f85" +
		"e91cf2d53dd25887a87f1606bc15a4791ee54e9f11257f9e3e1d71f3a008238e701d17d6226fd5a7fe4a0e42373d744ae6af6d93ea422d8c9b1af473" +
		"8f72f33d5b82532f9fcff1191d11c3fe1b87b0b15295e3a33f317adfe6a950bcf7ad35d55bd73e63d099807fdddf08d882e4a0e6974b918c22bfc05d" +
		"ecae1d555d41630f2f959fd9277bb32ee5955924633cfe074594719bf17695aaf79429628f048d32a7ee551b147dc40e799a642090a51c8f2f9ad35e" +
		"76712fe33e2010ba2c8ef2fe890cc8753f17a254b9581efc5b7155e435004ef34ddd37244b9a0bc8919da272c13a0d744731dfa66153769b77ab6197" +
		"3caf2d3c15d7b0cf231313a09cb39a5d27252a5b667f6a19fc94747be4b77b98998de54cdefcf11fe11ef1e788bd79cf262b2b959a714bced91f1264" +
		"cdb9965453126fd8919baa30208bfd8ff24a9d90a0b23fc66e19e813c48e4dea405cb20a7cfdd09cb87a6f88c80e47def9249a1f42e926c47b3cbff3" +
		"d96acab7e94c3ba17be9073e1bb9bf948f47047d1320f4841bc0260cbf309c61dd2866875177f4c81e631af649425a835c2bf9f04dabece048edb9ae" +
		"70008fec4ed3e535c2487ab7aa2c74f3893814b0eb65e21a7539429ee14d969eadfd909a14e952c041150a2466212708e18fde0a71663859eb0a5b02" +
		"4a487690a35a0b932200dfd9fa884feba08cb83d6acbfb384386fcb91307cbd90fd21d82ff60f4e6a3321ef96337e847de74b52b37a0771ea48bccb5" +
		"36aa91bed6713061d10f79adb613d099a2776dfb92d0fd63d96b5ff24bc1a788feebfeadfdd63e1bb0af694f9c15236b0152cc1984e38378c4b478e1" +
		"5b1fdec0eb363c38a916d52d38f7bd6ed6cbe95af7fb1f48bb2588661959a9095583fb455207bbc64f76051e19084770459802d9e71449e55ce12c22" +
		"e6de1d4728c4da7c95ea3b9b38125227b8d64a9159c179faebffd7266ee70fa1f80f3c500d380b47b9438efdf228b04ea40e026fea35958a387943f0" +
		"4d89ebf930aaab8c5a5b8339a641290a50cd27ac85a6f215849163ad263ee42cf51fda71ef7e766a8fc2a9cbd217e5a412fb257f32e8120867bc7a14" +
		"400720b56c902f3b586c0deb837af2b50aaa10bfc4599fb33bea22e025e58d8b65104437c937b0641738b86712f46337dfe473c9677de5e5a1aef294" +
		"9b3929e301b490dfe92285ce54f4b237809ae0935f0577f66c7478abfd60d2ca604cc8e555eb7eb62b05060433693a4131844bb3d013d783b7eb0892" +
		"7bd8eeecf202924f423602564aa91c66113a225040a236180245a1621f5a32d90bb6851cc03c2677c497f35d5136fb9089a1b595f6930b8242ec148f" +
		"44bf4191bfbff651b150d5d7a00d39f4c66426573cdaa0056003a25cbd77760ad2e63b8769bda6acd35c4d3e714486dba485858d417df135adaec422" +
		"849a48e73c13848517ec22dff539cb8855bd570796eafc76a69e0ed3dcdc424db3bf55c7d41bf16053b4b858b119d72f2319a7dc7496c992ff5802e3" +
		"02ec16695ffed47cac77b8e1b13cca8832d52a56e19bd2f2a0d57a96c094e66152afc0fd332a1f2067826ff9b144778a049b4cb9373c2b08f416c984" +
		"3ba15adf2dfb19ee136bf1536940e27634a56b54ed9f9961b5ccc29c3a7e8d477b70fe321b63e7cb61e600f83a988418b1801e33c947955d7c76c248" +
		"da0dfb2035342d668d85e8b0ba273cf267615dd271407ffda589beefee6c4bb138d3ddd88844225fbc1685cf4f80fb65aa05c7dec889de12435f8cce" +
		"1529e44e1529efd1189f857291185e6bd123e2e9968a51da8e41b3c97a39c0e816d08bb5731c115e8fc06607474c301f09695d3c1e9ffbe989b75671" +
		"6caf91e6bdc4aba8899c559db2565a8958f968376aa7ad7586305579f5290d8fe11c2fa8ee2fb06f20e0c26b358a74f80f21b1b24d862445f1dc1a97" +
		"aa5e649c08d71b7f3b148ac3d92a544bf50913908901d5243099d540bde11ab002f727603851f9554e96e01f66634f7e7d795baa54cb209d26f1c6ad" +
		"9232b27e22d3a9f12293695c564b42f65bfce307f2ab31e40dfd4d512ce28bdbc69526ecbd0b42ec8ce88058fa4a50818144e71f25f8c8ae799e8415" +
		"4f6ddcdab33862e9f2a431624c596399a3af31dc42075699b12fff20accdddd2bdc2f297427d308b14387540489f4ce8b64c992119694f6507d86fbe" +
		"b26a64154aee68e0b7b5740207bd5fb9c5322617f46f22561e45b4af7b96b7f7ce5b5adb7436faa7d145e18d5d841ad219cdcf74451bb8ed33017af4" +
		"9e4aad909180bc5e3282c9aa33124ece269deebd341642baf07b9b4f9dfe2b2c9d1ed9b286c74202297c8785d0d51e77e80617b9cbb5aff2b43ffb11" +
		"adbfe16f0a090940df89ccd3046672b531afa3cf7fa76231c3f954c18b7285f2ee6c911574cdc06201ca40e6a4302c997965f2d2e36340d053807826" +
		"7a8ee2385bd19604ab8fa42ac038ee1b6dae77155937a8b6d295642c1a489022ba293ed406ff6dca1df6a8da27898294b70f92c8150263ceab0af6f7" +
		"735d5c50745e5b0181e6928b7d9929a88f02838797deef80c539221be800860f2ba59dae2233299cd35ec79366577305e4385ff90f5dfab2549a63bf" +
		"42a125609a2fea45a8bc366aef972d18d5025abf70f74dfa17af27876fa00e53db3fa81547a23afd3eaf68c875df835ef243cc9134d47fc5ad948b90" +
		"e7d1997c1c2bf40ccbaaa3b463232e7137826612b882b2fe2dbb3d3bdae7666288a5125ae68b0848758818f4584c5a74f0c47e7dc47c52a6c09d8b62" +
		"02ad95763f87af01d3f5e8323c2c848e43e5a548bb7682b48853b2ebad196d23ee3d8368e52a13c6e431ff765051bd6e9393c4c45321bbba44ee86a3" +
		"1bcaab603ec5e90a487dfb0f1ab6d2629e6b0102afe89bc7cd10689b7662cf6f02734112d24f8335673ea3dc51c40ea98e46dba100892688535eae72" +
		"278ca0a6c9f6fea5b1274f45b15a4a2cf8fbb5876150904e28925742e5a1ccc2120ac996cc8f57c3559067d5d1b9730fe27b174b6b798fe2d855510b" +
		"f6f89378f8f6a01e3ebd6fab7a85e75359983560950bea7c24f9aed5a96084531b4b174c50556ee8cdf851b7575330f5c409570c87647067965758e7" +
		"e40a32f30c7b19ba04c8abe6dde54180f821ca800a0c81020b244cdc0150d4abff6ff5fd745b2fbeadc4e29de741fa73996a309db66c1cb8c9b4bfae" +
		"6c93eaa3a2124fc3f18816cd682896a933d28ff22aee56c1d3c9fc6547edcf32e7cdf8e0b2b35171a070d958910d5676bb47f3d4ba3b5b808fcf70b9" +
		"de07c0afec1629c353dec4eecda38f02b501d07e137c366884ce5466aae88f8ea60482a47645ed540fc97c36c2b96409afed6623e50de8b25f474430" +
		"82c60b23a83c73e89980cd866d53203246db1c551cdb1838c6e0c5c93bc46e109c17c8d7e2c98591a94b1425735dec7546d42dc30d450843366d1839" +
		"ec50e0ce08c54f25fdb51ae767f18030e0f86d3ae62b1787521906a78fc00224be5e6f904f26493ebe0e39ee8976336b5ef4f26479245ed33a13d839" +
		"6a02daf2fc55ed9e65c4e7e7e07843e4c057aedda411ff1ee558baebdea27a3e3c2eed7ad2caa252624d2bf48f4c15ad38b2ec8d3bdde3942f126fa5" +
		"deb30ecef538f68d15fedaf03765cff7440edd7269a1ce297db49c325a3aed99c313c773e3fe68450686274baf57314dd967bd70789f852de88b50eb" +
		"1b8e19fcecaf30701f14935cc99d8d7869208fcd63ee525dad550000a4be05b03fd6b5136de2a8608450f61b8557b82a6681af1e948da84d33f5af17" +
		"5008a8ef7569c83d0dea704e427b5b419e998690e3ee489f0b20e912f6eac8fcd40b8a2be63879d712e159a834eae816662c3ce418d6b79c1eb6918c" +
		"bad312bf5b6e671859a027f871fc307b7c35f56d06d1a360dd7c08082b82d0de80dc3e86373126f521ca54f07d36c678106401d6bfcce423251e20a7" +
		"367c05a2b30ea7e29a9825453bfe20aecf304cb2ca5832a0dd2f168d40ab047bc490cfea9afd2a20f7a2ef2cdba5c6173fabb5eecdd2e02506ee719d" +
		"1c05b22f335bc5f99971f244197ef9fc93ba327da8c6a1cfa2f178caca11b7c3560d03c151f7e4a2ea20e2f5092dc2f6aedc219cdfc3dae6bfe3a6de" +
		"71e12d27ae6e820da5fd9364c1164e9c5818bc29035c2c063426604dcab7d217253465ae7b4ac0ed8209c9ef4dd724dea2d5558870e0cd58888e94ff" +
		"6fbb2627543284470167cc9ba8cb5669987ec4e291d71c4e4a4d53837ad7143e0eaa3fee840cbe98827ccfaa1769cb83a66642d2abd2b468697cc1a3" +
		"dd7e606d7456c8119bd8b01fa0937ee570ebf55a565555ab220c2a1044838ad66b0eea670e9835812be848011997f568a4bf1efb55a9f425cd87629d" +
		"eee7dc4904366c1383596366f26b87e94c5731f48073948eac25639ed32e7160860d0f1c96d91f25c27971bc18dc65d32eac79f0deef9bd313893645" +
		"db4802ab0bd795295feb9720b3699aaebe8a789708ea6bd86183f241e5ce21426a0924d58812ed7cc3ef37dccf5a934f448a5acb04c11fcf7c8a57ea" +
		"4b1ccd6b35ff2efd947c9e05fb0d068a674328ae3fb09f2fb914a17454b1d9c67ad4b8c94bb9d8d44741f8ea50a1a1473350b4cab74205b28b1da2d6" +
		"ccc5c64650229aed7ceb506fb0a5934c1f940361133140a815678501350ecc6001c4d9d19a341999fdad785318f216ef3bf1f7f9ad4a49ebb5055d7f" +
		"562a540b7b5cd3e9792d45ca22fa4ff7f13d0b4e960fdeb0daa7519a2ccc636ed8ed54ea42d15f699a0c9714d67467ed1136409d81318bdc5c16cd4d" +
		"377df13387e43f57f0b0e7ca35835dca43ae0834a60a58d9171b8224627ea664b57bed05342fe219c82b87187c75360247b864ddc15d029959a58d99" +
		"a5a089030d8245f04d228308b48812ab187c7a80cb41f5816f4c1728b725d866ccc889968d26ef18d5ba2e206f5ef4321c6c9c8d843a80f4ca3c5147" +
		"a89cea912afc5dcedee23d7cfa5be41e2cd9080d05f58821dda896e2c233ba0c8693817ab34c35f5d1fa29ecff84eb77cf91acdef02191ec1c221515" +
		"74202c8038ad147e69550f9603d17e38ed31559791893a4a8ce4c97df3a261fa94d007ecb7b64a594c013d58623971eb4f2fbc9272bdf7dc0eb41013" +
		"1321b9419147f12534cec6b03af2e22076435653b6d9bdd577b89ac8256227eaffae2c24bb7e12e85f229c47880866fcd6430487574e068bb0cd748b" +
		"259a600000";

	private static readonly byte[] TransactionId = Convert.FromHexString(RustTransactionIdHex);
	private static readonly byte[] AssetId = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
	private static readonly byte[] WitnessBinding = Convert.FromHexString(RustWitnessBindingHex);
	private static readonly byte[] SpendPublicKey = Convert.FromHexString(SpendPublicKeyHex);
	private static readonly byte[] BlindingPublicKey = Convert.FromHexString(AlternatePublicKeyHex);

	[Fact]
	public void PreservesAsymmetricRustConsensusAndDigestVectors()
	{
		byte[] witnessInclusiveTransaction = Convert.FromHexString(RustWitnessInclusiveTransactionHex);
		LiquidOwnedOutputObservation observation = CreateObservation();

		Assert.Equal(WitnessBinding, SHA256.HashData(witnessInclusiveTransaction));
		Assert.Equal(TransactionId, observation.GetTransactionIdConsensusBytes());
		Assert.NotEqual(TransactionId.Reverse().ToArray(), observation.GetTransactionIdConsensusBytes());
		Assert.Equal(AssetId, observation.GetAssetIdConsensusBytes());
		Assert.NotEqual(AssetId.Reverse().ToArray(), observation.GetAssetIdConsensusBytes());
		Assert.Equal(WitnessBinding, observation.GetTransactionWitnessBinding());
		Assert.NotEqual(WitnessBinding.Reverse().ToArray(), observation.GetTransactionWitnessBinding());
		Assert.Equal(7u, observation.OutputIndex);
		Assert.Equal(LiquidKeyBranch.External, observation.Branch);
		Assert.Equal(17u, observation.DerivationIndex);
		Assert.Equal(123_456_789L, observation.Value);
	}

	[Fact]
	public void AcceptsClosedOutputIndexDerivationAndValueBoundaries()
	{
		LiquidOwnedOutputObservation minimum = CreateObservation(
			outputIndex: 0,
			derivationIndex: 0,
			value: 1);
		LiquidOwnedOutputObservation maximum = CreateObservation(
			outputIndex: LiquidOutPoint.MaxSpendableOutputIndex,
			derivationIndex: LiquidOwnedOutputObservation.MaxDerivationIndex,
			value: long.MaxValue);

		Assert.Equal(0u, minimum.OutputIndex);
		Assert.Equal(0u, minimum.DerivationIndex);
		Assert.Equal(1L, minimum.Value);
		Assert.Equal(LiquidOutPoint.MaxSpendableOutputIndex, maximum.OutputIndex);
		Assert.Equal(LiquidOwnedOutputObservation.MaxDerivationIndex, maximum.DerivationIndex);
		Assert.Equal(long.MaxValue, maximum.Value);
	}

	[Fact]
	public void RejectsOutputIndexDerivationAndValueOutsideClosedBounds()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => CreateObservation(
			outputIndex: LiquidOutPoint.MaxSpendableOutputIndex + 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => CreateObservation(
			derivationIndex: LiquidOwnedOutputObservation.MaxDerivationIndex + 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => CreateObservation(value: 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => CreateObservation(value: (ulong)long.MaxValue + 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => CreateObservation(value: ulong.MaxValue));
	}

	[Fact]
	public void AcceptsAnAllZeroWitnessBindingWithoutTreatingItAsAnIdentifier()
	{
		byte[] zeroDigest = new byte[LiquidTransactionWitnessBinding.ByteLength];

		LiquidOwnedOutputObservation observation = CreateObservation(witnessBinding: zeroDigest);

		Assert.Equal(zeroDigest, observation.GetTransactionWitnessBinding());
	}

	[Fact]
	public void RejectsInvalidSpendAndBlindingPublicKeys()
	{
		byte[] invalidPoint = new byte[LiquidBlindingPublicKey.CompressedByteLength];
		invalidPoint[0] = 0x02;

		ArgumentException invalidSpend = Assert.Throws<ArgumentException>(() =>
			LiquidOwnedOutputObservation.Create(
				TransactionId,
				7,
				WitnessBinding,
				ScriptFor(SpendPublicKey, LiquidKeyBranch.External, 17),
				invalidPoint,
				BlindingPublicKey,
				LiquidKeyBranch.External,
				17,
				AssetId,
				123_456_789));
		ArgumentException invalidBlinding = Assert.Throws<ArgumentException>(() =>
			CreateObservation(blindingPublicKey: invalidPoint));

		Assert.Throws<ArgumentException>(() => CreateObservation(spendPublicKey: []));
		Assert.Throws<ArgumentException>(() => CreateObservation(blindingPublicKey: []));
		Assert.DoesNotContain(Convert.ToHexString(invalidPoint), invalidSpend.ToString(), StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(Convert.ToHexString(invalidPoint), invalidBlinding.ToString(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void RejectsNonNativeOrMismatchedSpendScriptsAndUnknownBranches()
	{
		Assert.Throws<ArgumentException>(() => CreateObservation(scriptPubKey: [0x51]));

		byte[] alternateSpendPublicKey = Convert.FromHexString(AlternatePublicKeyHex);
		Assert.Throws<ArgumentException>(() => CreateObservation(
			scriptPubKey: ScriptFor(alternateSpendPublicKey, LiquidKeyBranch.External, 17)));

		Assert.Throws<ArgumentOutOfRangeException>(() => LiquidOwnedOutputObservation.Create(
			TransactionId,
			7,
			WitnessBinding,
			ScriptFor(SpendPublicKey, LiquidKeyBranch.External, 17),
			SpendPublicKey,
			BlindingPublicKey,
			(LiquidKeyBranch)2,
			17,
			AssetId,
			123_456_789));
	}

	[Fact]
	public void RejectsZeroTransactionAndAssetIdentifiers()
	{
		Assert.Throws<ArgumentException>(() => CreateObservation(transactionId: new byte[32]));
		Assert.Throws<ArgumentException>(() => CreateObservation(assetId: new byte[32]));
	}

	[Fact]
	public void DoesNotRetainCallerBuffersOrExposeMutableInternalBuffers()
	{
		byte[] transactionId = [.. TransactionId];
		byte[] witnessBinding = [.. WitnessBinding];
		byte[] scriptPubKey = ScriptFor(SpendPublicKey, LiquidKeyBranch.External, 17);
		byte[] spendPublicKey = [.. SpendPublicKey];
		byte[] blindingPublicKey = [.. BlindingPublicKey];
		byte[] assetId = [.. AssetId];
		byte[] expectedScript = [.. scriptPubKey];

		LiquidOwnedOutputObservation observation = LiquidOwnedOutputObservation.Create(
			transactionId,
			7,
			witnessBinding,
			scriptPubKey,
			spendPublicKey,
			blindingPublicKey,
			LiquidKeyBranch.External,
			17,
			assetId,
			123_456_789);

		transactionId.AsSpan().Clear();
		witnessBinding.AsSpan().Clear();
		scriptPubKey.AsSpan().Clear();
		spendPublicKey.AsSpan().Clear();
		blindingPublicKey.AsSpan().Clear();
		assetId.AsSpan().Clear();

		Assert.Equal(TransactionId, observation.GetTransactionIdConsensusBytes());
		Assert.Equal(WitnessBinding, observation.GetTransactionWitnessBinding());
		Assert.Equal(expectedScript, observation.GetScriptPubKey());
		Assert.Equal(SpendPublicKey, observation.GetSpendPublicKey());
		Assert.Equal(BlindingPublicKey, observation.GetBlindingPublicKey());
		Assert.Equal(AssetId, observation.GetAssetIdConsensusBytes());

		foreach (byte[] exported in new[]
		{
			observation.GetTransactionIdConsensusBytes(),
			observation.GetTransactionWitnessBinding(),
			observation.GetScriptPubKey(),
			observation.GetSpendPublicKey(),
			observation.GetBlindingPublicKey(),
			observation.GetAssetIdConsensusBytes(),
		})
		{
			exported.AsSpan().Clear();
		}

		Assert.Equal(TransactionId, observation.GetTransactionIdConsensusBytes());
		Assert.Equal(WitnessBinding, observation.GetTransactionWitnessBinding());
		Assert.Equal(expectedScript, observation.GetScriptPubKey());
		Assert.Equal(SpendPublicKey, observation.GetSpendPublicKey());
		Assert.Equal(BlindingPublicKey, observation.GetBlindingPublicKey());
		Assert.Equal(AssetId, observation.GetAssetIdConsensusBytes());
	}

	[Fact]
	public void EqualityAndHashBindEveryObservationField()
	{
		LiquidOwnedOutputObservation baseline = CreateObservation();
		LiquidOwnedOutputObservation equal = CreateObservation();
		byte[] changedTransactionId = [.. TransactionId];
		changedTransactionId[0] ^= 1;
		byte[] changedWitnessBinding = [.. WitnessBinding];
		changedWitnessBinding[0] ^= 1;
		byte[] changedAssetId = [.. AssetId];
		changedAssetId[0] ^= 1;
		byte[] alternatePublicKey = Convert.FromHexString(AlternatePublicKeyHex);

		LiquidOwnedOutputObservation[] changed =
		[
			CreateObservation(transactionId: changedTransactionId),
			CreateObservation(outputIndex: 8),
			CreateObservation(witnessBinding: changedWitnessBinding),
			CreateObservation(
				spendPublicKey: alternatePublicKey,
				scriptPubKey: ScriptFor(alternatePublicKey, LiquidKeyBranch.External, 17)),
			CreateObservation(blindingPublicKey: SpendPublicKey),
			CreateObservation(branch: LiquidKeyBranch.Internal),
			CreateObservation(derivationIndex: 18),
			CreateObservation(assetId: changedAssetId),
			CreateObservation(value: 123_456_790),
		];

		Assert.Equal(baseline, equal);
		Assert.Equal(baseline.GetHashCode(), equal.GetHashCode());
		foreach (LiquidOwnedOutputObservation variation in changed)
		{
			Assert.NotEqual(baseline, variation);
		}
	}

	[Fact]
	public void BlindingKeyUsesDefensiveValueEquality()
	{
		byte[] publicKey = [.. BlindingPublicKey];
		LiquidBlindingPublicKey first = LiquidBlindingPublicKey.Create(publicKey);
		LiquidBlindingPublicKey equal = LiquidBlindingPublicKey.Create(publicKey);
		LiquidBlindingPublicKey changed = LiquidBlindingPublicKey.Create(SpendPublicKey);
		publicKey.AsSpan().Clear();
		byte[] exported = first.GetCompressedPublicKey();
		exported.AsSpan().Clear();

		Assert.Equal(BlindingPublicKey, first.GetCompressedPublicKey());
		Assert.Equal(first, equal);
		Assert.Equal(first.GetHashCode(), equal.GetHashCode());
		Assert.NotEqual(first, changed);
	}

	[Fact]
	public void StringsAndErrorsDoNotRevealObservationFacts()
	{
		LiquidOwnedOutputObservation observation = CreateObservation();
		LiquidBlindingPublicKey blindingKey = LiquidBlindingPublicKey.Create(BlindingPublicKey);
		LiquidTransactionWitnessBinding witness = LiquidTransactionWitnessBinding.Create(WitnessBinding);
		var exception = Assert.Throws<ArgumentException>(() => CreateObservation(scriptPubKey: [0x51]));

		foreach (string text in new[]
		{
			observation.ToString(),
			blindingKey.ToString(),
			witness.ToString(),
			exception.ToString(),
		})
		{
			Assert.DoesNotContain(Convert.ToHexString(TransactionId), text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(Convert.ToHexString(AssetId), text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(Convert.ToHexString(WitnessBinding), text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(SpendPublicKeyHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(AlternatePublicKeyHex, text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("123456789", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void WalletStateAndDeltaDoNotAcceptOrPromoteObservations()
	{
		foreach (Type type in new[] { typeof(LiquidWalletTransactionDelta), typeof(LiquidWalletState) })
		{
			IEnumerable<MethodBase> callableMembers = type
				.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				.Cast<MethodBase>()
				.Concat(type.GetConstructors(
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

			Assert.DoesNotContain(callableMembers, member =>
				member.GetParameters().Any(parameter => ContainsObservationType(parameter.ParameterType)));
		}
	}

	[Fact]
	public void ObservationExposesNoWalletPromotionSurface()
	{
		Type[] forbiddenTypes =
		[
			typeof(LiquidOwnedOutput),
			typeof(LiquidAssetAmount),
			typeof(LiquidWalletTransactionDelta),
			typeof(LiquidWalletState),
		];
		MethodInfo[] declaredMethods = typeof(LiquidOwnedOutputObservation).GetMethods(
			BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
			BindingFlags.Public | BindingFlags.NonPublic);

		foreach (MethodInfo method in declaredMethods)
		{
			Assert.DoesNotContain(forbiddenTypes, forbidden => ContainsType(method.ReturnType, forbidden));
			Assert.DoesNotContain(method.GetParameters(), parameter =>
				forbiddenTypes.Any(forbidden => ContainsType(parameter.ParameterType, forbidden)));
		}
	}

	private static LiquidOwnedOutputObservation CreateObservation(
		byte[]? transactionId = null,
		uint outputIndex = 7,
		byte[]? witnessBinding = null,
		byte[]? scriptPubKey = null,
		byte[]? spendPublicKey = null,
		byte[]? blindingPublicKey = null,
		LiquidKeyBranch branch = LiquidKeyBranch.External,
		uint derivationIndex = 17,
		byte[]? assetId = null,
		ulong value = 123_456_789)
	{
		byte[] effectiveSpendPublicKey = spendPublicKey ?? SpendPublicKey;
		byte[] effectiveScript = scriptPubKey ?? ScriptFor(
			effectiveSpendPublicKey,
			branch,
			derivationIndex);
		return LiquidOwnedOutputObservation.Create(
			transactionId ?? TransactionId,
			outputIndex,
			witnessBinding ?? WitnessBinding,
			effectiveScript,
			effectiveSpendPublicKey,
			blindingPublicKey ?? BlindingPublicKey,
			branch,
			derivationIndex,
			assetId ?? AssetId,
			value);
	}

	private static byte[] ScriptFor(
		byte[] spendPublicKey,
		LiquidKeyBranch branch,
		uint derivationIndex) =>
		LiquidSpendKeyReference.Create(spendPublicKey, branch, derivationIndex).GetScriptPubKey();

	private static bool ContainsObservationType(Type type) =>
		ContainsType(type, typeof(LiquidOwnedOutputObservation));

	private static bool ContainsType(Type type, Type expected) =>
		type == expected ||
		(type.HasElementType && ContainsType(type.GetElementType()!, expected)) ||
		(type.IsGenericType && type.GetGenericArguments().Any(argument => ContainsType(argument, expected)));

}
