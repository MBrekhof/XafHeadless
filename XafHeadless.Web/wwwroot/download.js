// RPT-001: hand a byte stream from .NET to the browser as a file download.
//
// Needed because the collect endpoint is authenticated with a Bearer token, so a plain <a href> cannot
// reach it -- the browser would send the request without the Authorization header. The client therefore
// fetches the bytes in C# (where the token lives) and streams them here.
//
// This is Blazor's documented DotNetStreamReference pattern; the object URL is revoked immediately after
// the click so a large PDF is not pinned in memory for the life of the page.
window.downloadFileFromStream = async (fileName, contentStreamReference, contentType) => {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer], { type: contentType || 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName ?? 'download';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
};
