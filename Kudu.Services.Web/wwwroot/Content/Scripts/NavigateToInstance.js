jQuery(document).ready(function () {
    $.ajax({
        type: "GET",
        url: '/instance/all',
        success: function (response) {
            try {
                var obj = JSON.parse(response);
                var ul = document.getElementById("instances_tab_options");
                if (obj.length >= 1) {
                    for (var i = 0; i < obj.length; i++) {
                        var instanceTabBtn = document.createElement('li'); // is a node
                        instanceTabBtn.innerHTML = '<a href=\"#" class="dropdown-item"  onclick="NavigateToInstance(\'' + obj[i] + '\')">' + obj[i].substring(0, 6);
                        if (i != obj.length - 1) {
                            instanceTabBtn.innerHTML += '</a > <div class="dropdown-divider"></div>';
                        }
                        instanceTabBtn.setAttribute("id", "inst-id-btn-" + obj[i]);
                        if (obj[i].trim().valueOf() === $.currInst) {
                            $("#instance-drop-down-text").text('<i class="fas fa-caret-right"></i>' + obj[i].substring(0, 4));
                        }
                        ul.appendChild(instanceTabBtn);
                    }
                } else {
                    $("#instanceDropdownMenuButton").hide();
                }
            }
            catch (err) {
                console.log(err);
            }
        }
    });
});

function NavigateToInstance(instId) {
    try {
        if ($.currInst !== null && $.currInst === instId) {
            return;
        }
        // ping the root with new instance to update ARRAffinity 
        $.ajax({
            url: '/?instance=' + instId,
            type: 'GET',
            success: function (data) {
                $(".instances_tab_options_cls li.active").removeClass("active"); // reset all <li>to no active class
                $('#inst-id-btn-' + instId).addClass("active"); // add active class to curr instance btn <li> only
                location.reload();
            },
            error: function (request, error) {
                console.log("Navigating to instance failed: " + JSON.stringify(request));
            }
        });
    } catch (err) {
        console.log(err);
    }
}